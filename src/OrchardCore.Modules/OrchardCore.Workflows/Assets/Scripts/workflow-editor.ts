///<reference path="../Lib/jquery/typings.d.ts" />
///<reference path="../Lib/jsplumb/typings.d.ts" />

class WorkflowEditor {
    constructor(container: HTMLElement) {
        jsPlumb.ready(function () {
            var instance = jsPlumb.getInstance({

                DragOptions: { cursor: 'pointer', zIndex: 2000 },
                ConnectionOverlays: [
                    ["Arrow", {
                        location: 1,
                        visible: true,
                        width: 11,
                        length: 11
                    }],
                    ["Label", {
                        location: 0.1,
                        id: "label",
                        cssClass: "connection-label"
                    }]
                ],
                Container: container
            });

            var basicType = {
                connector: "StateMachine",
                paintStyle: { stroke: "red", strokeWidth: 4 },
                hoverPaintStyle: { stroke: "blue" },
                overlays: [
                    "Arrow"
                ]
            };

            instance.registerConnectionType("basic", basicType);

            // this is the paint style for the connecting lines..
            var connectorPaintStyle = {
                strokeWidth: 2,
                stroke: "#61B7CF",
                joinstyle: "round",
                outlineStroke: "white",
                outlineWidth: 2
            };
            
            // .. and this is the hover style.
            var connectorHoverStyle = {
                strokeWidth: 3,
                stroke: "#216477",
                outlineWidth: 5,
                outlineStroke: "white"
            };
            
            var endpointHoverStyle = {
                fill: "#216477",
                stroke: "#216477"
            };
                
            // the definition of source endpoints (the small blue ones)
            var sourceEndpoint = {
                endpoint: "Dot",
                paintStyle: {
                    stroke: "#7AB02C",
                    fill: "#7AB02C",
                    radius: 7,
                    strokeWidth: 1
                },
                isSource: true,
                connector: ["Flowchart", { stub: [40, 60], gap: 10, cornerRadius: 5, alwaysRespectStubs: true }],
                connectorStyle: connectorPaintStyle,
                hoverPaintStyle: endpointHoverStyle,
                connectorHoverStyle: connectorHoverStyle,
                dragOptions: {},
                overlays: [
                    ["Label", {
                        location: [0.5, 1.5],
                        label: "Drag",
                        cssClass: "endpointSourceLabel",
                        visible: false
                    }]
                ]
            };
            
            // the definition of target endpoints (will appear when the user drags a connection)
            var targetEndpoint = {
                endpoint: "Dot",
                paintStyle: { fill: "#7AB02C", radius: 7 },
                hoverPaintStyle: endpointHoverStyle,
                maxConnections: -1,
                dropOptions: { hoverClass: "hover", activeClass: "active" },
                isTarget: true,
                overlays: [
                    ["Label", { location: [0.5, -0.5], label: "Drop", cssClass: "endpointTargetLabel", visible: false }]
                ]
            };
                
            var init = function (connection: Connection) {
                connection.getOverlay("label").setLabel(connection.sourceId.substring(15) + "-" + connection.targetId.substring(15));
            };

            var addEndpoints = function (toId: string, sourceAnchors: Array<string>, targetAnchors: Array<string>) {
                for (var i = 0; i < sourceAnchors.length; i++) {
                    var sourceUUID: string = toId + sourceAnchors[i];
                    instance.addEndpoint("flowchart" + toId, sourceEndpoint, {
                        anchor: sourceAnchors[i], uuid: sourceUUID
                    });
                }
                //for (var j = 0; j < targetAnchors.length; j++) {
                //    var targetUUID = toId + targetAnchors[j];
                //    instance.addEndpoint("flowchart" + toId, targetEndpoint, { anchor: targetAnchors[j], uuid: targetUUID });
                //}
            };

            // suspend drawing and initialise.
            instance.batch(function () {

                addEndpoints("Window4", ["TopCenter", "BottomCenter"], ["LeftMiddle", "RightMiddle"]);
                addEndpoints("Window2", ["LeftMiddle", "BottomCenter"], ["TopCenter", "RightMiddle"]);
                addEndpoints("Window3", ["RightMiddle", "BottomCenter"], ["LeftMiddle", "TopCenter"]);
                addEndpoints("Window1", ["LeftMiddle", "RightMiddle"], ["TopCenter", "BottomCenter"]);

                // listen for new connections; initialise them the same way we initialise the connections at startup.
                instance.bind("connection", function (connInfo, originalEvent) {
                    init(connInfo.connection);
                });

                
                $(container).find(".activity").each(function (this: HTMLElement) {

                    // Make the activity draggable.
                    instance.draggable(this, { grid: [20, 20] });

                    // Configure the activity as target.
                    instance.makeTarget(this, {
                        dropOptions: { hoverClass: "hover" },
                        anchor: "Top",
                        endpoint:[ "Dot", { radius: 8 } ]
                    });
                });

                // connect a few up
                instance.connect({ uuids: ["Window2BottomCenter", "Window3TopCenter"], editable: true });
                instance.connect({ uuids: ["Window2LeftMiddle", "Window4LeftMiddle"], editable: true });
                instance.connect({ uuids: ["Window4TopCenter", "Window4RightMiddle"], editable: true });
                instance.connect({ uuids: ["Window3RightMiddle", "Window2RightMiddle"], editable: true });
                instance.connect({ uuids: ["Window4BottomCenter", "Window1TopCenter"], editable: true });
                instance.connect({ uuids: ["Window3BottomCenter", "Window1BottomCenter"], editable: true });

                instance.bind("click", function (conn, originalEvent) {
                    instance.deleteConnection(conn);
                });

                instance.bind("connectionDrag", function (connection) {
                    console.log("connection " + connection.id + " is being dragged. suspendedElement is ", connection.suspendedElement, " of type ", connection.suspendedElementType);
                });

                instance.bind("connectionDragStop", function (connection) {
                    console.log("connection " + connection.id + " was dragged");
                });

                instance.bind("connectionMoved", function (params) {
                    console.log("connection " + params.connection.id + " was moved");
                });
            });

            this.jsPlumbInstance = instance;
        });
    }

    private jsPlumbInstance: jsPlumbInstance;
}

$.fn.workflowEditor = function (this: JQuery): JQuery {
    let workflowEditor = new WorkflowEditor(this[0]);
    return this;
};

$(document).ready(function () {
    $('.workflow-editor-canvas').workflowEditor();
});