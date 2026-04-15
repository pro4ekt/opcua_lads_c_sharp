using Opc.Ua;
using Opc.Ua.Server;
using System.IO;
using Opc.Ua.Export;
using System.Threading;
using System.Threading.Tasks;

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
        // Добавляем поля для хранения ссылок на узлы переменных и токен отмены задачи
        private BaseDataVariableState _currentVolumeVar;
        private BaseDataVariableState _speedVar;
        private CancellationTokenSource _pipettingCts;

        /// <summary>
        /// Конструктор NodeManager-а для нашего устройства. 
        /// Мы передаем базовому классу пространство имен (URI), за которое он отвечает.
        /// </summary>
        public PipetteNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, "http://lab.server/Pipette/")
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
                // Ищем файл Pipette.xml по относительному пути (в проекте или рядом с .exe)
                string nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "NodeSet", "Pipette.xml");
                if (!File.Exists(nodeSetFilePath))
                {
                    // Попытка найти рядом с exe, если запущено не из студии (после публикации)
                    nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "NodeSet", "Pipette.xml");
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
                var rootNodeId = new NodeId("Pipette", ns);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, rootNodeId));

                // Создаем обратные ссылки (чтобы от дочерних объектов можно было перейти к родительским)
                AddReverseReferences(externalReferences);

                // 4. Привязываем C# логику к методам и переменным (БИНДИНГ КОЛЛБЕКОВ)
                AttachLogicHandlers();
            }
        }

        /// <summary>
        /// В этом методе мы "оживляем" простое дерево объектов, привязывая делегаты (коллбеки)
        /// к переменным и методам, которые были загружены из XML.
        /// Без этого метода устройство было бы только для чтения (или методы просто ничего бы не делали).
        /// </summary>
        private void AttachLogicHandlers()
        {
            ushort ns = NamespaceIndexes[0];

            // Сохраняем ссылки на переменные, чтобы менять их из C# логики
            _currentVolumeVar = FindPredefinedNode(new NodeId("Pipette_DeviceComponent_CurrentVolume", ns), typeof(BaseDataVariableState)) as BaseDataVariableState;
            _speedVar = FindPredefinedNode(new NodeId("Pipette_Functions_Speed", ns), typeof(BaseDataVariableState)) as BaseDataVariableState;

           /*  // Находим метод ConfigurePipetting по его NodeId (из XML) и привязываем C# делегат
            if (FindPredefinedNode(new NodeId("PipetteDevice_PipettingFunction_ConfigurePipetting", ns), typeof(MethodState)) is MethodState configureMethod)
            {
                configureMethod.OnCallMethod = Method_OnCall;
            }
            */

            // Находим метод StartPipetting
            if (FindPredefinedNode(new NodeId("Pipette_Functions_StartPipetting", ns), typeof(MethodState)) is MethodState startMethod)
            {
                startMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод AbortPipetting
            if (FindPredefinedNode(new NodeId("Pipette_Functions_AbortPipetting", ns), typeof(MethodState)) is MethodState abortMethod)
            {
                abortMethod.OnCallMethod = Method_OnCall;
            }

            // Находим переменную Speed (которая доступна для записи AccessLevel=3) и привязываем перехват при изменении
            if (_speedVar != null)
            {
                _speedVar.OnWriteValue = OnVariableWrite; // Коллбек сработает, когда клиент по сети пришлет новое значение
            }
        }

        /// <summary>
        /// Вызывается автоматически, когда клиент (مثلاً, UaExpert) изменяет значение переменной и отправляет его серверу.
        /// </summary>
        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Pipette Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            // Тут можно передать команду на "железо", чтобы пипетка сменила скорость
            return StatusCodes.Good; // Подтверждаем клиенту, что запись прошла успешно
        }

        /// <summary>
        /// Вызывается автоматически, когда клиент запускает RPC-метод на сервере.
        /// </summary>
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Pipette Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "StartPipetting")
            {
                StartPipettingTask();
            }
            else if (method.BrowseName.Name == "AbortPipetting")
            {
                // Останавливаем фоновый процесс, если нажали Abort
                _pipettingCts?.Cancel();
                Console.WriteLine("[Pipette]: Pipetting manually aborted.");
            }

            return StatusCodes.Good;
        }

        /// <summary>
        /// Фоновая задача (Thread), которая уменьшает объем раз в секунду.
        /// </summary>
        private void StartPipettingTask()
        {
            // Отменяем предыдущую задачу, если она была запущена
            _pipettingCts?.Cancel();
            _pipettingCts = new CancellationTokenSource();
            var token = _pipettingCts.Token;

            // Запускаем асинхронно в фоне, чтобы не блокировать сетевой поток OPC UA
            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        double currentVol;
                        ushort speed;

                        // Обязательно блокируем Lock, так как сервер читает эти данные параллельно
                        lock (Lock)
                        {
                            currentVol = (double)_currentVolumeVar.Value;
                            speed = (ushort)_speedVar.Value;
                        }

                        if (currentVol <= 0)
                        {
                            Console.WriteLine("[Pipette]: Target volume reached (0).");
                            break;
                        }

                        // Уменьшаем объем на значение скорости
                        currentVol -= speed;
                        if (currentVol < 0) currentVol = 0; // Защита от отрицательных значений

                        // Записываем новые значения в адресное пространство сервера
                        lock (Lock)
                        {
                            _currentVolumeVar.Value = currentVol;
                            // Обязательно обновляем метку времени и уведомляем подписчиков(клиентов)!
                            _currentVolumeVar.Timestamp = DateTime.UtcNow;
                            _currentVolumeVar.ClearChangeMasks(SystemContext, false);
                        }

                        Console.WriteLine($"[Pipette Process]: Current Volume = {currentVol}");

                        // Ждем 1 секунду до следующего шага
                        await Task.Delay(1000, token);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Задача была отменена токеном
                }
            }, token);
        }
    }
}
