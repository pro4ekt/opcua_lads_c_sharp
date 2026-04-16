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
        // Добавляем поля для хранения ссылок на узлы переменных и токен отмены задачи
        private CancellationTokenSource _spinningCts;
        
        /// <summary>
        /// Конструктор NodeManager-а для нашего устройства. 
        /// Мы передаем базовому классу пространство имен (URI), за которое он отвечает.
        /// </summary>
        public CentrifugeNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, "http://lab.server/Centrifuge/")
        {
            SystemContext.NodeIdFactory = this; // Говорим контексту, что этот менеджер будет создавать NodeId
        }
        
        /// <summary>
        /// Этот метод вызывается ядром сервера при его старте. 
        /// Здесь мы строим наше адресное пространство: парсим XML, добавляем объекты в память и делаем их видимыми по сети.
        /// </summary>
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                // Убеждаемся, что в словаре есть базовая папка ObjectsFolder (это "корень" для всех объектов на сервере).
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                // 1. Читаем структуру из XML файла (ПАРСИНГ)
                // Ищем файл Centrifuge.xml по относительному пути (в проекте или рядом с .exe)
                string nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "NodeSet", "Centrifuge.xml");
                if (!File.Exists(nodeSetFilePath))
                {
                    // Попытка найти рядом с exe, если запущено не из студии (после публикации)
                    nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "NodeSet", "Centrifuge.xml");
                }

                // Коллекция для временного хранения объектов, загруженных из файла
                NodeStateCollection predefinedNodes = new NodeStateCollection();
                using (Stream stream = File.OpenRead(nodeSetFilePath))
                {
                    // UANodeSet.Read выполняет физическое чтение XML.
                    UANodeSet nodeSet = UANodeSet.Read(stream);
                    // Import "разворачивает" XML теги в объекты C# (NodeState) и помещает их в коллекцию
                    nodeSet.Import(SystemContext, predefinedNodes);
                }

                // 2. Добавляем загруженные узлы в наше адресное пространство (ПУБЛИКАЦИЯ В ПАМЯТИ)
                // Передаем каждый узел внутрь базового менеджера, чтобы он начал их обслуживать
                for (int i = 0; i < predefinedNodes.Count; i++)
                {
                    AddPredefinedNode(SystemContext, predefinedNodes[i]);
                }

                // 3. Создаем связь между стандартным корнем сервера (ObjectsFolder) и нашим главным устройством
                // Иначе клиент просто не "дотянется" до нашего дерева, оно будет висеть в вакууме.
                ushort ns = NamespaceIndexes[0];
                var rootNodeId = new NodeId("Centrifuge", ns);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, rootNodeId));

                // Создаем обратные ссылки (чтобы от дочерних объектов можно было перейти к родительским)
                AddReverseReferences(externalReferences);

                // 4. Привязываем C# логику к методам и переменным (БИНДИНГ КОЛЛБЕКОВ)
                AttachLogicHandlers();
            }
        }
        
        /// <summary>
        /// Вызывается автоматически, когда клиент (مثلاً, UaExpert) изменяет значение переменной и отправляет его серверу.
        /// </summary>
        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            // Тут можно передать команду на "железо", чтобы пипетка сменила скорость
            return StatusCodes.Good; // Подтверждаем клиенту, что запись прошла успешно
        }
        
        /// <summary>
        /// В этом методе мы "оживляем" простое дерево объектов, привязывая делегаты (коллбеки)
        /// к переменным и методам, которые были загружены из XML.
        /// Без этого метода устройство было бы только для чтения (или методы просто ничего бы не делали).
        /// </summary>
        private void AttachLogicHandlers()
        {
            ushort ns = NamespaceIndexes[0];

           /*  // Находим метод ConfigurePipetting по его NodeId (из XML) и привязываем C# делегат
            if (FindPredefinedNode(new NodeId("PipetteDevice_PipettingFunction_ConfigurePipetting", ns), typeof(MethodState)) is MethodState configureMethod)
            {
                configureMethod.OnCallMethod = Method_OnCall;
            }
            */

            // Находим метод StartSpinning
            if (FindPredefinedNode(new NodeId("Centrifuge_Functions_StartSpinning", ns), typeof(MethodState)) is MethodState startMethod)
            {
                startMethod.OnCallMethod = Method_OnCall;
            }

            // Находим StopSpinning
            if (FindPredefinedNode(new NodeId("Centrifuge_Functions_StopSpinning", ns), typeof(MethodState)) is MethodState abortMethod)
            {
                abortMethod.OnCallMethod = Method_OnCall;
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
                //StartSpinningTask();
            }
            else if (method.BrowseName.Name == "StopSpinning")
            {
                // Останавливаем фоновый процесс, если нажали Abort
                _spinningCts?.Cancel();
                Console.WriteLine("[Centrifuge]: Spinning manually aborted.");
            }

            return StatusCodes.Good;
        }

    }

}

