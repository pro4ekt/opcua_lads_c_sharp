using Opc.Ua;
using Opc.Ua.Server;
using System.IO;
using Opc.Ua.Export;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace OpcUa.Lads.Foundation.Server
{
    public class PipetteNodeManagerFactory : INodeManagerFactory
    {
        public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        {
            return new PipetteNodeManager(server, configuration);
        }

        public StringCollection NamespacesUris => ["http://lab.server/Pipette/"];
    }

    public class PipetteNodeManager : CustomNodeManager2
    {
        // Токен отмены задачи
        private CancellationTokenSource _pipettingCts;

        /// <summary>
        /// Конструктор NodeManager-а для нашего устройства. 
        /// </summary>
        public PipetteNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, 
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Pipette/")
        {
            SystemContext.NodeIdFactory = this; // Говорим контексту, что этот менеджер будет создавать NodeId
            NamespaceUris =
            [
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Pipette/"
            ];
        }

        /// <summary>
        /// Этот метод вызывается ядром сервера при его старте. 
        /// </summary>
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                // Убеждаемся, что в словаре есть базовая папка ObjectsFolder
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                // Ассемблея, где лежат базовые xml файлы
                var foundationAssembly = typeof(OpcUa.Lads.Foundation.Server.NodeManager).Assembly;

                // Импортируем зависимости Opc.Ua (DI -> AMB -> Machinery -> LADS)
                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.DI.NodeSet2.xml");
                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.AMB.NodeSet2.xml");
                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.Machinery.NodeSet2.xml");
                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.LADS.NodeSet2.xml");

                // Читаем структуру из XML файла Pipette.xml
                var pipetteAssembly = typeof(PipetteNodeManager).Assembly;
                ImportXmlResource(externalReferences, pipetteAssembly, "PipetteServer.Pipette.xml");

                AddReverseReferences(externalReferences);

                // Привязываем C# логику к методам и переменным
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

        /// <summary>
        /// Привязываем делегаты (коллбеки) к переменным и методам, которые были загружены из XML.
        /// </summary>
        private void AttachLogicHandlers()
        {
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Pipette/");

            // Находим метод StartPipetting (ns=5; i=7017)
            if (FindPredefinedNode(new NodeId(7017u, ns), typeof(MethodState)) is MethodState startMethod)
            {
                startMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод StopPipetting (ns=5; i=7018)
            if (FindPredefinedNode(new NodeId(7018u, ns), typeof(MethodState)) is MethodState stopMethod)
            {
                stopMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод Stop внутри DispenseController (ns=5; i=7024)
            if (FindPredefinedNode(new NodeId(7024u, ns), typeof(MethodState)) is MethodState dcStopMethod)
            {
                dcStopMethod.OnCallMethod = Method_OnCall;
            }

            // Привязываем коллбеки на изменение writable-переменных
            uint[] writableVariableIds = 
            [
                6018u, // AssetId
                6201u, // DispenseController: TargetValue (целевой объем)
                6203u  // DispenseController: FlowRate (через что мы задаем скорость)
            ];

            foreach (var varId in writableVariableIds)
            {
                if (FindPredefinedNode(new NodeId(varId, ns), typeof(BaseVariableState)) is BaseVariableState variableNode)
                {
                    variableNode.OnWriteValue = new NodeValueEventHandler(OnVariableWrite);
                }
            }
        }

        /// <summary>
        /// Вызывается автоматически, когда клиент изменяет значение переменной.
        /// </summary>
        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Pipette Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            // Тут можно передать команду на железо, если меняется условный FlowRate или TargetValue
            return StatusCodes.Good; 
        }

        /// <summary>
        /// Вызывается автоматически, когда клиент запускает RPC-метод.
        /// </summary>
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Pipette Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "StartPipetting")
            {
                StartPipettingTask();
            }
            else if (method.BrowseName.Name == "StopPipetting" || method.BrowseName.Name == "Stop")
            {
                // Останавливаем фоновый процесс, если вызван стоп
                _pipettingCts?.Cancel();
                Console.WriteLine("[Pipette]: Pipetting manually aborted.");
            }

            return StatusCodes.Good;
        }

        /// <summary>
        /// Фоновая задача (Thread), которая имитирует работу пипетки.
        /// </summary>
        private void StartPipettingTask()
        {
            _pipettingCts?.Cancel();
            _pipettingCts = new CancellationTokenSource();
            var token = _pipettingCts.Token;

            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Pipette/");
            
            // Находим узлы, чтобы обновлять их значения для клиентов OPC UA
            var currentStateNode = FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) as BaseVariableState;    // State Machine
            var currentVolumeNode = FindPredefinedNode(new NodeId(6200u, ns), typeof(BaseVariableState)) as BaseVariableState;   // CurrentValue
            var totalizedVolumeNode = FindPredefinedNode(new NodeId(6202u, ns), typeof(BaseVariableState)) as BaseVariableState; // TotalizedValue

            Task.Run(async () =>
            {
                try
                {
                    // 1. Меняем статус на "Running" (в процессе аспирации/диспенсинга)
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Running"));
                    double currentVolume = 0;

                    // Читаем TotalizedValue, если он там есть
                    double totalVolume = 0;
                    if (totalizedVolumeNode?.Value is double existingTotal) totalVolume = existingTotal; 

                    // Имитируем процесс набора жидкости (допустим 5 итераций по 10 uL)
                    for (int i = 0; i < 5; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        // 2. Имитируем набор объема
                        currentVolume += 10.5; // Набираем 10.5 мкл за шаг
                        totalVolume += 10.5;
                        
                        UpdateNodeValue(currentVolumeNode, currentVolume);
                        UpdateNodeValue(totalizedVolumeNode, totalVolume);

                        Console.WriteLine($"[Pipette]: Aspirating process... Volume in tip: {currentVolume} uL");
                        await Task.Delay(1000, token);
                    }

                    // 3. Успешное завершение: выливаем жидкость и ставим статус "Complete"
                    Console.WriteLine("[Pipette]: Dispensing everything...");
                    await Task.Delay(1000, token);
                    token.ThrowIfCancellationRequested();

                    UpdateNodeValue(currentVolumeNode, 0.0); // Сбросили жидкость
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                    Console.WriteLine("[Pipette]: Pipetting Completed successfully.");
                }
                catch (OperationCanceledException)
                {
                    // 4. Остановка пользователем (Aborted)
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                    UpdateNodeValue(currentVolumeNode, 0.0); // Сбрасываем жидкость при ошибке/отмене
                    Console.WriteLine("[Pipette]: Pipetting manually aborted. State -> Aborted");
                }
            }, token);
        }

        // Вспомогательный метод для обновления значения ноды и уведомления подписчиков (клиентов)
        private void UpdateNodeValue(BaseVariableState node, object newValue)
        {
            if (node != null)
            {
                node.Value = newValue;
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false); // Сообщаем серверу, что значение изменено
            }
        }
    }
}