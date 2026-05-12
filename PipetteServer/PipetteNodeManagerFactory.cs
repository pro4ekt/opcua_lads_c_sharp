using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Export;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace OpcUa.Lads.Foundation.Server;

public class PipetteNodeManagerFactory : INodeManagerFactory
{
    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
    {
        return new PipetteNodeManager(server, configuration);
    }

    public StringCollection NamespacesUris => ["http://Pipette"];
}

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

            // Загрузка стандартных словарей LADS (Учитывая, что AMB больше нет в Pipette.xml)
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.DI.NodeSet2.xml");
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.Machinery.NodeSet2.xml");
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.LADS.NodeSet2.xml");

            // Загрузка модели Pipette
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

        // Инициализируем начальное состояние (Idle) при запуске сервера
        var currentStateNode = FindPredefinedNode(new NodeId(6096u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (currentStateNode != null && currentStateNode.Value == null)
        {
            currentStateNode.Value = new Opc.Ua.LocalizedText("en", "Idle");
        }

        // Подключаем обработчик изменения переменной TargetValue (NodeId: 6093)
        var targetVolumeNode = FindPredefinedNode(new NodeId(6093u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (targetVolumeNode != null)
        {
            if (targetVolumeNode.Value == null)
            {
                targetVolumeNode.Value = 0.0; // Инициализируем дефолтным значением
            }
            // Даем права на запись (AccessLevel и UserAccessLevel)
            targetVolumeNode.AccessLevel = AccessLevels.CurrentReadOrWrite;
            targetVolumeNode.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            
            targetVolumeNode.OnSimpleWriteValue = OnTargetVolume_Write;
        }

        // Инициализируем CurrentValue (NodeId: 6092) дефолтным значением
        var currentVolumeNode = FindPredefinedNode(new NodeId(6092u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (currentVolumeNode != null && currentVolumeNode.Value == null)
        {
            currentVolumeNode.Value = 0.0;
        }

        // Подключаем методы из FunctionalUnitState (Aspirate, Dispense, AttachTip, EjectTip)
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
        Console.WriteLine($"[Pipette]: Client requested to change 'Target Value' to => {parsedValue}");
        
        // Пример логики: если клиент пытается задать объем больше 100, мы можем выдать предупреждение или отклонить
        try
        {
            double vol = Convert.ToDouble(parsedValue);
            if (vol > 100.0)
            {
                Console.WriteLine("[Pipette]: Warning! Requested volume is unusually high!");
                // Если мы хотим запретить запись, можно раскомментировать строку ниже:
                // return StatusCodes.BadOutOfRange;
            }
        }
        catch (Exception)
        {
            Console.WriteLine("[Pipette]: Failed to parse the new Target Value.");
        }

        return StatusCodes.Good;
    }

    private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
    {
        ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://Pipette");
        
        // NodeId 6036 - Это CurrentState внутри FunctionalUnitState
        var currentStateNode = FindPredefinedNode(new NodeId(6096u, ns), typeof(BaseVariableState)) as BaseVariableState;
        
        // NodeId 6092 - Это CurrentValue внутри VolumeControl
        var currentVolumeNode = FindPredefinedNode(new NodeId(6092u, ns), typeof(BaseVariableState)) as BaseVariableState;
        
        // NodeId 6093 - Это TargetValue внутри VolumeControl
        var targetVolumeNode = FindPredefinedNode(new NodeId(6093u, ns), typeof(BaseVariableState)) as BaseVariableState;

        var stateBefore = currentStateNode?.Value as Opc.Ua.LocalizedText;
        Console.WriteLine($"[Pipette]: Method '{method.BrowseName.Name}' called. Current State: {stateBefore?.Text ?? "Unknown"}");

        // Запуск асинхронной задачи в зависимости от метода
        StartDeviceTask(method.BrowseName.Name, currentStateNode, currentVolumeNode, targetVolumeNode);

        Console.WriteLine($"[Pipette]: Command '{method.BrowseName.Name}' dispatched.");

        return StatusCodes.Good;
    }

    private void StartDeviceTask(string commandName, BaseVariableState currentStateNode, BaseVariableState currentVolumeNode, BaseVariableState targetVolumeNode)
    {
        _taskCts?.Cancel();
        _taskCts = new CancellationTokenSource();
        var token = _taskCts.Token;

        Task.Run(async () =>
        {
            try
            {
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Running"));
                Console.WriteLine($"[Pipette]: Starting '{commandName}' operation... State -> Running");
                
                // Целевой объем или симулятивный набор (по умолчанию)
                double volumeValue = 0.0;
                if (targetVolumeNode?.Value != null)
                {
                    volumeValue = Convert.ToDouble(targetVolumeNode.Value);
                }

                // Симуляция логики в зависимости от вызванного метода
                if (commandName == "Aspirate")
                {
                    Console.WriteLine($"[Pipette]: Drawing {volumeValue} ml...");
                    await Task.Delay(2000, token); 
                    UpdateNodeValue(currentVolumeNode, volumeValue);
                }
                else if (commandName == "Dispense")
                {
                    Console.WriteLine($"[Pipette]: Dispensing {volumeValue} ml...");
                    await Task.Delay(2000, token); 
                    UpdateNodeValue(currentVolumeNode, 0.0); // Сбрасываем до нуля
                }
                else 
                {
                    // Для других методов (AttachTip, EjectTip) просто ждем
                    await Task.Delay(1500, token);
                }

                token.ThrowIfCancellationRequested();

                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                Console.WriteLine($"[Pipette]: '{commandName}' operation Complete. State -> Complete");
                
                // Автоматический возврат в Idle
                await Task.Delay(1500, token);
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
                Console.WriteLine("[Pipette]: Ready for next command. State -> Idle");
            }
            catch (OperationCanceledException)
            {
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                Console.WriteLine($"[Pipette]: '{commandName}' operation was cancelled. State -> Aborted");
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
            node.ClearChangeMasks(SystemContext, false); // Обязательно для уведомления подписок клиента
        }
    }
}