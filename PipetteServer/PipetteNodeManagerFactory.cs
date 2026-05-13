using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Export;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace OpcUa.Lads.Foundation.Server;

/// <summary>
/// Фабрика менеджера узлов. Класс отвечает за интеграцию логики в основной OPC UA Сервер.
/// </summary>
public class PipetteNodeManagerFactory : INodeManagerFactory
{
    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
    {
        return new PipetteNodeManager(server, configuration);
    }

    public StringCollection NamespacesUris => ["http://Pipette"];
}

/// <summary>
/// Менеджер узлов для управления адресным пространством и бизнес-логикой устройства в соответствии со стандартом LADS.
/// </summary>
public class PipetteNodeManager : CustomNodeManager2
{
    private CancellationTokenSource _taskCts;

    public PipetteNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
        : base(server, configuration, 
            "http://opcfoundation.org/UA/DI/",       
            "http://opcfoundation.org/UA/Machinery/",
            "http://opcfoundation.org/UA/LADS/",     
            "http://Pipette")                        
    {
        SystemContext.NodeIdFactory = this;
        
        NamespaceUris =
        [
            "http://opcfoundation.org/UA/DI/",
            "http://opcfoundation.org/UA/Machinery/",
            "http://opcfoundation.org/UA/LADS/",
            "http://Pipette"
        ];
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }

            var foundationAssembly = typeof(OpcUa.Lads.Foundation.Server.NodeManager).Assembly;

            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.DI.NodeSet2.xml");
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.Machinery.NodeSet2.xml");
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.LADS.NodeSet2.xml");

            var currentAssembly = typeof(PipetteNodeManager).Assembly;
            ImportXmlResource(externalReferences, currentAssembly, "PipetteServer.Pipette.xml");

            AddReverseReferences(externalReferences);
            AttachLogicHandlers();
        }
    }

    private void ImportXmlResource(IDictionary<NodeId, IList<IReference>> externalReferences, System.Reflection.Assembly assembly, string resourcePath)
    {
        using var stream = assembly.GetManifestResourceStream(resourcePath);
        if (stream == null) 
        {
            throw new Exception($"Cannot find embedded resource: {resourcePath} in assembly {assembly.FullName}");
        }
        
        var nodeSet = UANodeSet.Read(stream);
        
        foreach (var nameSpace in nodeSet.NamespaceUris)
        {
            SystemContext.NamespaceUris.GetIndexOrAppend(nameSpace);
        }

        var predefinedNodes = new NodeStateCollection();
        nodeSet.Import(SystemContext, predefinedNodes);
        
        var toImportNodes = new List<NodeState>();
        foreach (var node in predefinedNodes)
        {
            if (node is BaseTypeState state && state.SuperTypeId != null &&
                node.NodeId.NamespaceIndex == state.SuperTypeId.NamespaceIndex &&
                !PredefinedNodes.ContainsKey(state.SuperTypeId))
            {
                toImportNodes.Add(node);
            }
            else
            {
                AddPredefinedNode(SystemContext, node);
            }
        }

        foreach (var node in toImportNodes)
        {
            AddPredefinedNode(SystemContext, node);
        }
    }

    private void AttachLogicHandlers()
    {
        ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://Pipette");

        var currentStateNode = FindPredefinedNode(new NodeId(6036u, ns), typeof(BaseVariableState)) as BaseVariableState;
        var controlStateNode = FindPredefinedNode(new NodeId(6096u, ns), typeof(BaseVariableState)) as BaseVariableState;
        
        if (currentStateNode != null && currentStateNode.Value == null)
            UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
            
        if (controlStateNode != null && controlStateNode.Value == null)
            UpdateNodeValue(controlStateNode, new Opc.Ua.LocalizedText("en", "Idle"));

        var targetVolumeNode = FindPredefinedNode(new NodeId(6093u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (targetVolumeNode != null)
        {
            if (targetVolumeNode.Value == null)
            {
                UpdateNodeValue(targetVolumeNode, 0.0);
            }
            targetVolumeNode.OnSimpleWriteValue = OnTargetVolume_Write;
        }

        var currentVolumeNode = FindPredefinedNode(new NodeId(6092u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (currentVolumeNode != null)
        {
            if (currentVolumeNode.Value == null) UpdateNodeValue(currentVolumeNode, 0.0);
        }

        var tipCurrentNode = FindPredefinedNode(new NodeId(6118u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (tipCurrentNode != null)
        {
            if (tipCurrentNode.Value == null) UpdateNodeValue(tipCurrentNode, true);
        }

        if (FindPredefinedNode(new NodeId(7004u, ns), typeof(MethodState)) is MethodState aspirateMethod)
            aspirateMethod.OnCallMethod = Method_OnCall;

        if (FindPredefinedNode(new NodeId(7005u, ns), typeof(MethodState)) is MethodState dispenseMethod)
            dispenseMethod.OnCallMethod = Method_OnCall;

        if (FindPredefinedNode(new NodeId(7006u, ns), typeof(MethodState)) is MethodState attachTipMethod)
            attachTipMethod.OnCallMethod = Method_OnCall;

        if (FindPredefinedNode(new NodeId(7007u, ns), typeof(MethodState)) is MethodState ejectTipMethod)
            ejectTipMethod.OnCallMethod = Method_OnCall;
    }

    private ServiceResult OnTargetVolume_Write(ISystemContext context, NodeState node, ref object parsedValue)
    {
        var targetVolumeNode = node as BaseVariableState;
        Console.WriteLine($"[Pipette]: Client requested to change 'Target Value' to => {parsedValue}");
        
        try
        {
            double vol = Convert.ToDouble(parsedValue);
            parsedValue = vol; 

            if (vol > 100.0)
            {
                Console.WriteLine("[Pipette]: Warning! Requested volume exceeds standard limits.");
            }

            UpdateNodeValue(targetVolumeNode, vol);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pipette]: Failed to parse Target Value: {ex.Message}");
            return StatusCodes.BadTypeMismatch; 
        }

        return StatusCodes.Good;
    }

    private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
    {
        ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://Pipette");
        
        var currentStateNode = FindPredefinedNode(new NodeId(6036u, ns), typeof(BaseVariableState)) as BaseVariableState;
        var currentVolumeNode = FindPredefinedNode(new NodeId(6092u, ns), typeof(BaseVariableState)) as BaseVariableState;
        var targetVolumeNode = FindPredefinedNode(new NodeId(6093u, ns), typeof(BaseVariableState)) as BaseVariableState;
        var tipCurrentNode = FindPredefinedNode(new NodeId(6118u, ns), typeof(BaseVariableState)) as BaseVariableState;

        var stateBefore = currentStateNode?.Value as Opc.Ua.LocalizedText;
        Console.WriteLine($"[Pipette]: Method '{method.BrowseName.Name}' called. Current State: {stateBefore?.Text ?? "Unknown"}");

        if ((method.BrowseName.Name == "Aspirate" || method.BrowseName.Name == "Dispense") && tipCurrentNode?.Value is false)
        {
            Console.WriteLine("[Pipette]: Error! Action rejected. No tip attached.");
            return StatusCodes.BadInvalidState; 
        }

        StartDeviceTask(method.BrowseName.Name, currentStateNode, currentVolumeNode, targetVolumeNode, tipCurrentNode);

        Console.WriteLine($"[Pipette]: Command '{method.BrowseName.Name}' dispatched.");
        return StatusCodes.Good;
    }

    private void StartDeviceTask(string commandName, BaseVariableState currentStateNode, BaseVariableState currentVolumeNode, BaseVariableState targetVolumeNode, BaseVariableState tipCurrentNode)
    {
        _taskCts?.Cancel();
        _taskCts = new CancellationTokenSource();
        var token = _taskCts.Token;

        Task.Run(async () =>
        {
            try
            {
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Running"));
                Console.WriteLine($"[Pipette]: State Machine -> Running");
                
                double targetVol = 0.0;
                if (targetVolumeNode?.Value != null) targetVol = Convert.ToDouble(targetVolumeNode.Value);
                
                double currentVol = 0.0;
                if (currentVolumeNode?.Value != null) currentVol = Convert.ToDouble(currentVolumeNode.Value);

                if (commandName == "Aspirate") 
                {
                    Console.WriteLine($"[Pipette]: Drawing fluid up to {targetVol} ml...");
                    while (currentVol < targetVol)
                    {
                        currentVol += 1.0; 
                        if (currentVol > targetVol) currentVol = targetVol;
                        UpdateNodeValue(currentVolumeNode, currentVol); 
                        await Task.Delay(200, token); 
                    }
                }
                else if (commandName == "Dispense") 
                {
                    Console.WriteLine($"[Pipette]: Dispensing fluid down to {targetVol} ml...");
                    while (currentVol > targetVol)
                    {
                        currentVol -= 1.0; 
                        if (currentVol < targetVol) currentVol = targetVol;
                        UpdateNodeValue(currentVolumeNode, currentVol); 
                        await Task.Delay(200, token);
                    }
                }
                else if (commandName == "AttachTip") 
                {
                    Console.WriteLine($"[Pipette]: Attaching Tip...");
                    await Task.Delay(1000, token); 
                    UpdateNodeValue(tipCurrentNode, true); 
                }
                else if (commandName == "EjectTip") 
                {
                    Console.WriteLine($"[Pipette]: Ejecting Tip...");
                    await Task.Delay(1000, token); 
                    UpdateNodeValue(tipCurrentNode, false); 
                    
                    UpdateNodeValue(currentVolumeNode, 0.0);
                    Console.WriteLine($"[Pipette]: Volume reset to 0.0 due to tip ejection.");
                }

                token.ThrowIfCancellationRequested();

                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                Console.WriteLine($"[Pipette]: Operation Complete. State Machine -> Complete");
                
                await Task.Delay(1500, token);
                
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
                Console.WriteLine("[Pipette]: State Machine -> Idle");
            }
            catch (OperationCanceledException)
            {
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                Console.WriteLine($"[Pipette]: Operation cancelled. State -> Aborted");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pipette]: Error: {ex.Message}");
            }
        }, token); 
    }

    private void UpdateNodeValue(BaseVariableState node, object newValue)
    {
        if (node != null) 
        {
            node.Value = newValue;
            node.Timestamp = DateTime.UtcNow;
            node.StatusCode = StatusCodes.Good;
            
            node.ClearChangeMasks(SystemContext, false);
        }
    }
}