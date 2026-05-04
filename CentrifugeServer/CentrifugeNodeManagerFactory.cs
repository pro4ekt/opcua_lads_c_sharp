using Opc.Ua;
using Opc.Ua.Server;
using System.IO;
using Opc.Ua.Export;
using System.Threading;
using System.Threading.Tasks;

namespace OpcUa.Lads.Foundation.Server
{
    public class CentrifugeNodeManagerFactory : INodeManagerFactory
    {
        public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        {
            return new CentrifugeNodeManager(server, configuration);
        }

        public StringCollection NamespacesUris => ["http://lab.server/Centrifuge/"];
    }

    public class CentrifugeNodeManager : CustomNodeManager2
    {
        public Action OnStartProgramCalled { get; set; }
        
        // Добавляем поля для хранения ссылок на узлы переменных и токен отмены задачи
        private CancellationTokenSource _spinningCts;
        
        /// <summary>
        /// Конструктор NodeManager-а для нашего устройства. 
        /// </summary>
        public CentrifugeNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, 
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Centrifuge/")
        {
            SystemContext.NodeIdFactory = this; // Говорим контексту, что этот менеджер будет создавать NodeId
            NamespaceUris =
            [
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Centrifuge/"
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

                // 1. Читаем структуру из XML файла Centrifuge.xml (ПАРСИНГ)
                var centrifugeAssembly = typeof(CentrifugeNodeManager).Assembly;
                ImportXmlResource(externalReferences, centrifugeAssembly, "CentrifugeServer.Centrifuge.xml");

                // 4. Привязываем C# логику
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

            AddReverseReferences(externalReferences);
        }
        
        /// <summary>
        /// Вызывается автоматически, когда клиент (مثلاً, UaExpert) изменяет значение переменной и отправляет его серверу.
        /// </summary>
        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good; // Подтверждаем клиенту, что запись прошла успешно
        }
        
        /// <summary>
        /// В этом методе мы "оживляем" простое дерево объектов, привязывая делегаты (коллбеки)
        /// к переменным и методам, которые были загружены из XML.
        /// Без этого метода устройство было бы только для чтения (или методы просто ничего бы не делали).
        /// </summary>
        private void AttachLogicHandlers()
        {
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Centrifuge/");

            // Находим метод StartSpinning (в XML это i=7017)
            if (FindPredefinedNode(new NodeId(7017u, ns), typeof(MethodState)) is MethodState startSpinningMethod)
            {
                startSpinningMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод StopSpinning (в XML это i=7018)
            if (FindPredefinedNode(new NodeId(7018u, ns), typeof(MethodState)) is MethodState stopSpinningMethod)
            {
                stopSpinningMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод Stop (в XML это i=7024 - внутри SpeedSensor -> Operational)
            if (FindPredefinedNode(new NodeId(7024u, ns), typeof(MethodState)) is MethodState stopSpeedMethod)
            {
                stopSpeedMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод Stop (в XML это i=7025 - внутри TemperatureSensor -> Operational)
            if (FindPredefinedNode(new NodeId(7025u, ns), typeof(MethodState)) is MethodState stopTempMethod)
            {
                stopTempMethod.OnCallMethod = Method_OnCall;
            }

            // Привязываем коллбеки на изменение переменных
            uint[] writableVariableIds = 
            [
                6018u, // AssetId
                6201u, // SpeedSensor: TargetValue
                6203u, // SpeedSensor: Speed
                6301u, // TemperatureSensor: TargetValue
                6303u  // TemperatureSensor: Temperature
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
        /// Вызывается автоматически, когда клиент запускает RPC-метод на сервере.
        /// </summary>
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "StartSpinning")
            {
                OnStartProgramCalled?.Invoke();
                StartSpinningTask();
            }
            else if (method.BrowseName.Name == "StopSpinning" || method.BrowseName.Name == "Stop")
            {
                // Останавливаем фоновый процесс, если вызван StopSpinning или Stop
                _spinningCts?.Cancel();
                Console.WriteLine("[Centrifuge]: Spinning manually aborted.");
            }

            return StatusCodes.Good;
        }

        private void StartSpinningTask()
        {
            _spinningCts?.Cancel();
            _spinningCts = new CancellationTokenSource();
            var token = _spinningCts.Token;
            
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Centrifuge/");
            
            // Находим узлы, чтобы обновлять их значения для клиентов OPC UA
            var currentStateNode = FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) as BaseVariableState;
            var speedCurrentValueNode = FindPredefinedNode(new NodeId(6200u, ns), typeof(BaseVariableState)) as BaseVariableState;

            Task.Run(async () =>
            {
                try
                {
                    // 1. Статус меняется на "Running" (указываем пространство имён Opc.Ua)
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Running"));
                    double currentSpeed = 0;

                    // Допустим, мы крутим 10 секунд
                    for (int i = 0; i < 10; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        // 2. Имитируем набор скорости
                        currentSpeed += 500; 
                        UpdateNodeValue(speedCurrentValueNode, currentSpeed);

                        Console.WriteLine($"[Centrifuge]: Spinning... Speed: {currentSpeed} RPM");
                        await Task.Delay(1000, token);
                    }

                    // 3. Успешное завершение (Complete)
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                    Console.WriteLine("[Centrifuge]: Spinning Completed Successfully.");
                    UpdateNodeValue(speedCurrentValueNode, 0.0); // Остановились
                }
                catch (OperationCanceledException)
                {
                    // 4. Остановка пользователем (Aborted)
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                    Console.WriteLine("[Centrifuge]: Spinning manually aborted. State -> Aborted");
                    UpdateNodeValue(speedCurrentValueNode, 0.0); 
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
