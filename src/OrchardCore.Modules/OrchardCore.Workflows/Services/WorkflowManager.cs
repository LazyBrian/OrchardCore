using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OrchardCore.Entities;
using OrchardCore.Workflows.Indexes;
using OrchardCore.Workflows.Models;
using YesSql;
using ActivityRecord = OrchardCore.Workflows.Models.Activity;

namespace OrchardCore.Workflows.Services
{
    public class WorkflowManager : IWorkflowManager
    {
        private readonly IActivitiesManager _activitiesManager;
        private readonly ISession _session;
        private readonly ILogger _logger;

        public WorkflowManager
        (
            IActivitiesManager activitiesManager,
            ISession session,
            ILogger<WorkflowManager> logger
        )
        {
            _activitiesManager = activitiesManager;
            _session = session;
            _logger = logger;
        }

        public async Task TriggerEvent(string name, IEntity target, Func<Dictionary<string, object>> contextFunc)
        {
            var context = contextFunc();
            var activity = _activitiesManager.GetActivityByName(name);

            if (activity == null)
            {
                _logger.LogError("Activity {0} was not found", name);
                return;
            }

            // Look for workflow definitions with a corresponding starting activity.
            var workflowsToStart =
                (await _session
                    .Query<WorkflowDefinition, WorkflowDefinitionByStartActivityIndex>(index =>
                        index.HasStart &&
                        index.StartActivityName == name &&
                        index.IsEnabled)
                    .ListAsync()).ToArray();

            // And any running workflow paused on this kind of activity for the specified target.
            // When an activity is restarted, all the other ones of the same workflow are cancelled.
            var awaitingWorkflowInstanceIndexQuery = _session
                .QueryIndex<WorkflowInstanceByAwaitingActivitiesIndex>(index =>
                    index.ActivityName == name &&
                    index.ActivityIsStart == false);

            var awaitingWorkflowInstanceIndexes = (await awaitingWorkflowInstanceIndexQuery.ListAsync()).ToArray();

            // If no activity record is matching the event, do nothing.
            if (workflowsToStart.Length == 0 && awaitingWorkflowInstanceIndexes.Length == 0)
            {
                return;
            }

            // Resume halted workflows.
            var awaitingWorkflowInstanceIds = awaitingWorkflowInstanceIndexes.Select(x => x.WorkflowInstanceId).ToArray();
            var awaitingWorkflowInstances = (await _session.GetAsync<WorkflowInstance>(awaitingWorkflowInstanceIds)).ToDictionary(x => x.Id);

            foreach (var awaitingWorkflowInstanceIndex in awaitingWorkflowInstanceIndexes)
            {
                var workflowInstance = awaitingWorkflowInstances[awaitingWorkflowInstanceIndex.WorkflowInstanceId];
                var workflowDefinition = await _session.GetAsync<WorkflowDefinition>(workflowInstance.DefinitionId);
                var activityRecord = workflowDefinition.Activities.SingleOrDefault(x => x.Id == awaitingWorkflowInstanceIndex.ActivityId);

                var workflowContext = new WorkflowContext
                {
                    WorkflowInstance = workflowInstance,
                    WorkflowDefinition = workflowDefinition
                };

                var activityContext = CreateActivityContext(activityRecord);

                // Check the CanExecute condition.
                try
                {
                    if (!activity.CanExecute(workflowContext, activityContext))
                    {
                        continue;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError("Error while evaluating an activity condition on {0}: {1}", name, e.ToString());
                    continue;
                }

                await ResumeWorkflow(workflowInstance, activityRecord, workflowContext);
            }

            // Start new workflows.
            foreach (var workflowToStart in workflowsToStart)
            {
                var workflowDefinition = (await _session.GetAsync<WorkflowDefinition>(workflowToStart.Id));
                var startActivity = workflowDefinition.Activities.FirstOrDefault(x => x.IsStart);
                var workflowInstance = new WorkflowInstance
                {
                    DefinitionId = workflowToStart.Id
                };
                var workflowContext = new WorkflowContext
                {
                    WorkflowDefinition = workflowDefinition,
                    WorkflowInstance = workflowInstance,
                };
                var activityContext = CreateActivityContext(startActivity);

                // Check the condition.
                try
                {
                    if (!activity.CanExecute(workflowContext, activityContext))
                    {
                        continue;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError("Error while evaluating an activity condition on {0}: {1}", name, e.ToString());
                    continue;
                }

                await StartWorkflow(workflowContext, startActivity);
            }
        }

        private ActivityContext CreateActivityContext(ActivityRecord activity)
        {
            return new ActivityContext
            {
                Record = activity,
                Activity = _activitiesManager.GetActivityByName(activity.Name),
                State = new Lazy<dynamic>(() => DeserializeState(activity.State))
            };
        }

        private async Task StartWorkflow(WorkflowContext workflowContext, ActivityRecord activity)
        {
            // Signal every activity that the workflow is about to start.
            var cancellationToken = new CancellationToken();
            InvokeActivities(a => a.OnWorkflowStarting(workflowContext, cancellationToken));

            if (cancellationToken.IsCancellationRequested)
            {
                // Workflow is aborted.
                return;
            }

            // Signal every activity that the workflow is has started.
            InvokeActivities(a => a.OnWorkflowStarted(workflowContext));

            var blockedOn = (await ExecuteWorkflow(workflowContext, activity)).ToList();

            // is the workflow halted on a blocking activity ?
            if (blockedOn.Count == 0)
            {
                // no, nothing to do
            }
            else
            {
                // workflow halted, create a workflow state
                _session.Save(workflowContext.WorkflowDefinition);

                foreach (var blocking in blockedOn)
                {
                    var awaitingActivity = new AwaitingActivity
                    {
                        ActivityId = blocking.Id,
                        IsStart = blocking.IsStart,
                        Name = blocking.Name
                    };
                    workflowContext.WorkflowInstance.AwaitingActivities.Add(awaitingActivity);
                }
            }
        }

        private async Task ResumeWorkflow(WorkflowInstance workflowInstance, ActivityRecord blockingActivity, WorkflowContext workflowContext)
        {
            // Signal every activity that the workflow is about to be resumed.
            var cancellationToken = new CancellationToken();
            InvokeActivities(a => a.OnWorkflowResuming(workflowContext, cancellationToken));

            if (cancellationToken.IsCancellationRequested)
            {
                // Workflow is aborted.
                return;
            }

            // Signal every activity that the workflow is resumed.
            InvokeActivities(activity => activity.OnWorkflowResumed(workflowContext));

            // Remove the awaiting activity.
            var awaitingActivity = workflowInstance.AwaitingActivities.FirstOrDefault(x => x.ActivityId == blockingActivity.Id);
            workflowInstance.AwaitingActivities.Remove(awaitingActivity);

            // Resume the workflow at the specified blocking activity.
            var blockedOn = (await ExecuteWorkflow(workflowContext, blockingActivity)).ToList();

            // Check if the workflow halted on any blocking activities, and if there are no more awaiting activities.
            if (blockedOn.Count == 0 && workflowInstance.AwaitingActivities.Count == 0)
            {
                // No, delete the workflow.
                _session.Delete(workflowInstance);
            }
            else
            {
                // Add the new ones.
                foreach (var blocking in blockedOn)
                {
                    workflowInstance.AwaitingActivities.Add(AwaitingActivity.FromActivity(blocking));
                }
            }
        }

        public async Task<IEnumerable<ActivityRecord>> ExecuteWorkflow(WorkflowContext workflowContext, ActivityRecord activity)
        {
            var firstPass = true;
            var scheduled = new Stack<ActivityRecord>();

            scheduled.Push(activity);

            var blocking = new List<ActivityRecord>();

            while (scheduled.Count > 0)
            {
                activity = scheduled.Pop();

                var activityContext = CreateActivityContext(activity);

                // While there is an activity to process.
                if (!firstPass)
                {
                    if (activityContext.Activity is IEvent)
                    {
                        blocking.Add(activity);
                        continue;
                    }
                }
                else
                {
                    firstPass = false;
                }

                // Signal every activity that the activity is about to be executed.
                var cancellationToken = new CancellationToken();
                InvokeActivities(a => a.OnActivityExecuting(workflowContext, activityContext, cancellationToken));

                if (cancellationToken.IsCancellationRequested)
                {
                    // Activity is aborted.
                    continue;
                }

                var outcomes = (await activityContext.Activity.Execute(workflowContext, activityContext)).ToList();

                // Signal every activity that the activity is executed.
                InvokeActivities(a => a.OnActivityExecuted(workflowContext, activityContext));

                foreach (var outcome in outcomes)
                {
                    var definition = await _session.GetAsync<WorkflowDefinition>(workflowContext.WorkflowDefinition.Id);

                    // Look for next activity in the graph.
                    var transition = definition.Transitions.FirstOrDefault(x => x.SourceActivityId == activity.Id && x.SourceEndpoint == outcome.Value);

                    if (transition != null)
                    {
                        var destinationActivity = workflowContext.WorkflowDefinition.Activities.SingleOrDefault(x => x.Id == transition.DestinationActivityId);
                        scheduled.Push(destinationActivity);
                    }
                }
            }

            // apply Distinct() as two paths could block on the same activity
            return blocking.Distinct();
        }

        /// <summary>
        /// Executes a specific action on all the activities of a workflow, using a specific context
        /// </summary>
        private void InvokeActivities(Action<IActivity> action)
        {
            foreach (var activity in _activitiesManager.GetActivities())
            {
                action(activity);
            }
        }

        private dynamic DeserializeState(string state)
        {
            return JObject.Parse(state);
        }
    }
}
