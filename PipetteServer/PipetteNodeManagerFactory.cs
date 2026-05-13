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
/// Фабрика менеджера узлов. Класс отвечает за интеграцию нашей логики пипетки в основной OPC UA Сервер.
/// Сервер при запуске ищет реализации INodeManagerFactory и вызывает метод Create.
/// </summary>
public class PipetteNodeManagerFactory : INodeManagerFactory
{
    // Метод создания самого NodeManager, в который передаются настройки сервера
    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
    {
        // Возвращаем наш кастомный класс менеджера узлов для пипетки
        return new PipetteNodeManager(server, configuration);
    }

    // Указываем корневое пространство имен, за которое отвечает эта фабрика
    public StringCollection NamespacesUris => ["http://Pipette"];
}

/// <summary>
/// Главный класс, управляющий адресным пространством и логикой работы нашей Пипетки.
/// Наследуется от CustomNodeManager2 — встроенного класса OPC UA SDK для создания своих узлов.
/// </summary>
public class PipetteNodeManager : CustomNodeManager2
{
    // Глобальный токен для отмены асинхронных задач (чтобы можно было прервать предыдущую команду, если пришла новая)
    private CancellationTokenSource _taskCts;

    // Конструктор инициализирует базовый класс, передавая ему необходимые пространства имен, которые мы будем использовать
    public PipetteNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
        : base(server, configuration, 
            "http://opcfoundation.org/UA/DI/",       // Стандарт Device Integration (DI)
            "http://opcfoundation.org/UA/Machinery/",// Стандарт Machinery
            "http://opcfoundation.org/UA/LADS/",     // Стандарт LADS (Laboratory Analytical Device Standard)
            "http://Pipette")                        // Наше кастомное пространство имен Пипетки
    {
        // Указываем, что данный класс сам выступает как фабрика идентификаторов узлов (NodeId)
        SystemContext.NodeIdFactory = this;
        
        // Регистрируем пространства имен в общую коллекцию при создании менеджера
        NamespaceUris =
        [
            "http://opcfoundation.org/UA/DI/",
            "http://opcfoundation.org/UA/Machinery/",
            "http://opcfoundation.org/UA/LADS/",
            "http://Pipette"
        ];
    }

    /// <summary>
    /// Этот метод вызывается ядром сервера при самом старте. Здесь мы физически загружаем XML-файлы в оперативную память.
    /// </summary>
    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        // Блокируем доступ к внешним ссылкам (externalReferences), чтобы другой поток случайно их не изменил в этот момент
        lock (Lock)
        {
            // Проверяем, создана ли папка Objects (корневая папка OPC UA). Если нет - инициализируем пустой список ссылок для нее
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }

            // Получаем ссылку на текущую сборку (сборку текущего проекта OpcUa.Lads.Foundation.Server), чтобы из нее вытащить встроенные XML ресурсы
            var foundationAssembly = typeof(OpcUa.Lads.Foundation.Server.NodeManager).Assembly;

            // Загружаем по очереди стандартные словари из XML-файлов (DI, Machinery, LADS). Они должны быть встроены как Embedded Resource в свойствах файла.
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.DI.NodeSet2.xml");
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.Machinery.NodeSet2.xml");
            ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.LADS.NodeSet2.xml");

            // Получаем ссылку на сборку конкретно этого файла (PipetteServer)
            var currentAssembly = typeof(PipetteNodeManager).Assembly;
            // Загружаем саму модель нашей пипетки
            ImportXmlResource(externalReferences, currentAssembly, "PipetteServer.Pipette.xml");

            // Автоматически добавляем обратные ссылки (например, если есть HasComponent от А к Б, сервер сгенерирует ComponentOf от Б к А)
            AddReverseReferences(externalReferences);
            
            // Запускаем наш кастомный метод, чтобы привязать C# код (функции и события) к узлам из XML
            AttachLogicHandlers();
        }
    }

    /// <summary>
    /// Вспомогательный метод: читает встроенный XML-файл и импортирует все описанные в нем узлы в сервер.
    /// </summary>
    private void ImportXmlResource(IDictionary<NodeId, IList<IReference>> externalReferences, System.Reflection.Assembly assembly, string resourcePath)
    {
        // Читаем файл из сборки (Embedded Resource) как поток байтов
        using var stream = assembly.GetManifestResourceStream(resourcePath);
        if (stream == null) 
        {
            // Если файл забыли отметить как "Embedded Resource" в Rider/Visual Studio, выбрасываем ошибку
            throw new Exception($"Cannot find embedded resource: {resourcePath} in assembly {assembly.FullName}");
        }
        
        // Парсим XML-поток с помощью стандартного класса UANodeSet
        var nodeSet = UANodeSet.Read(stream);
        
        // Пробегаемся по всем пространствам имен внутри XML-файла и регистрируем/получаем их индекс на нашем живом сервере
        foreach (var nameSpace in nodeSet.NamespaceUris)
        {
            SystemContext.NamespaceUris.GetIndexOrAppend(nameSpace);
        }

        // Создаем коллекцию для узлов и импортируем их из распарсенного XML
        var predefinedNodes = new NodeStateCollection();
        nodeSet.Import(SystemContext, predefinedNodes);
        
        var toImportNodes = new List<NodeState>();
        // Перебираем загруженные узлы: некоторые должны загрузиться позже своих "родительских" типов, поэтому разделяем их
        foreach (var node in predefinedNodes)
        {
            // Проверяем, является ли узел описанием базового типа, и если родитель еще не загружен — откладываем импорт
            if (node is BaseTypeState state && state.SuperTypeId != null &&
                node.NodeId.NamespaceIndex == state.SuperTypeId.NamespaceIndex &&
                !PredefinedNodes.ContainsKey(state.SuperTypeId))
            {
                toImportNodes.Add(node);
            }
            else
            {
                // Иначе сразу добавляем узел в системный контекст сервера (регистрируем его)
                AddPredefinedNode(SystemContext, node);
            }
        }

        // Загружаем те узлы, которые были отложены (теперь их родительские классы уже существуют)
        foreach (var node in toImportNodes)
        {
            AddPredefinedNode(SystemContext, node);
        }
    }

    /// <summary>
    /// Связывает узлы, загруженные из XML в память, с реальными C#-функциями и алгоритмами.
    /// </summary>
    private void AttachLogicHandlers()
    {
        // Получаем динамический индекс (NamespaceIndex) нашего пространства "http://Pipette". 
        // В зависимости от порядка загрузки он может быть 3 или 4 и тд.
        ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://Pipette");

        // 1. ИЩЕМ И ИНИЦИАЛИЗИРУЕМ INITIAL STATE (ТЕКУЩЕЕ СОСТОЯНИЕ)
        // Ищем узел с ID = 6036 в нашем NameSpace (ns) - это CurrentState от FunctionalUnitState, 
        // на котором висят методы и за которым следит клиент!
        var currentStateNode = FindPredefinedNode(new NodeId(6036u, ns), typeof(BaseVariableState)) as BaseVariableState;
        
        // Дополнительно можно инициализировать 6096 (ControlFunctionState), если вдруг клиент смотрит и туда
        var controlStateNode = FindPredefinedNode(new NodeId(6096u, ns), typeof(BaseVariableState)) as BaseVariableState;
        
        if (currentStateNode != null && currentStateNode.Value == null)
            UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
            
        if (controlStateNode != null && controlStateNode.Value == null)
            UpdateNodeValue(controlStateNode, new Opc.Ua.LocalizedText("en", "Idle"));

        // 2. ИЩЕМ TARGET VALUE (ЦЕЛЕВОЙ ОБЪЕМ)
        // Находим переменную целевого объема по ID 6093
        var targetVolumeNode = FindPredefinedNode(new NodeId(6093u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (targetVolumeNode != null)
        {
            if (targetVolumeNode.Value == null)
            {
                UpdateNodeValue(targetVolumeNode, 0.0);
            }
            
            targetVolumeNode.OnSimpleWriteValue = OnTargetVolume_Write;
        }

        // 3. ИЩЕМ CURRENT VALUE (ТЕКУЩИЙ ОБЪЕМ)
        // Находим переменную текущего объема по ID 6092, ставим 0.0, если пустая.
        var currentVolumeNode = FindPredefinedNode(new NodeId(6092u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (currentVolumeNode != null)
        {
            if (currentVolumeNode.Value == null) UpdateNodeValue(currentVolumeNode, 0.0);
        }

        // 3.5 ИЩЕМ TIP CONTROLLER (Наличие наконечника)
        // 6118 - CurrentValue для TipController
        var tipCurrentNode = FindPredefinedNode(new NodeId(6118u, ns), typeof(BaseVariableState)) as BaseVariableState;
        if (tipCurrentNode != null)
        {
            if (tipCurrentNode.Value == null) UpdateNodeValue(tipCurrentNode, true);
        }

        // 4. ПРИВЯЗЫВАЕМ МЕТОДЫ (ВЫЗОВЫ ИЗ КЛИЕНТА ПЕРЕНАПРАВЛЯЕМ В ФУНКЦИЮ C#)
        // Ищем метод Aspirate (ID 7004). Если клиент вызывает его -> запускаем C# метод "Method_OnCall"
        if (FindPredefinedNode(new NodeId(7004u, ns), typeof(MethodState)) is MethodState aspirateMethod)
            aspirateMethod.OnCallMethod = Method_OnCall;

        // Повторяем то же самое для методов Dispense(7005), AttachTip(7006), EjectTip(7007)
        if (FindPredefinedNode(new NodeId(7005u, ns), typeof(MethodState)) is MethodState dispenseMethod)
            dispenseMethod.OnCallMethod = Method_OnCall;

        if (FindPredefinedNode(new NodeId(7006u, ns), typeof(MethodState)) is MethodState attachTipMethod)
            attachTipMethod.OnCallMethod = Method_OnCall;

        if (FindPredefinedNode(new NodeId(7007u, ns), typeof(MethodState)) is MethodState ejectTipMethod)
            ejectTipMethod.OnCallMethod = Method_OnCall;
    }

    /// <summary>
    /// Функция-перехватчик. Вызывается сервером АВТОМАТИЧЕСКИ, когда клиент присылает команду на запись 
    /// нового значения в переменную TargetValue.
    /// </summary>
    private ServiceResult OnTargetVolume_Write(ISystemContext context, NodeState node, ref object parsedValue)
    {
        var targetVolumeNode = node as BaseVariableState;
        Console.WriteLine($"[Pipette]: Current Target Value = {targetVolumeNode?.Value}.");
        
        // Выводим в консоль запрос клиента
        Console.WriteLine($"[Pipette]: Client requested to change 'Target Value' to => {parsedValue}");
        
        try
        {
            // Убеждаемся, что значение строго типа Double, как того требует OPC UA.
            double vol = Convert.ToDouble(parsedValue);
            parsedValue = vol; // Перезаписываем ссылку на отформатированный Double

            if (vol > 100.0)
            {
                Console.WriteLine("[Pipette]: Warning! Requested volume is unusually high!");
            }

            // ПРИНУДИТЕЛЬНО ПРИМЕНЯЕМ ЗНАЧЕНИЕ ЗДЕСЬ, чтобы оно 100% зафиксировалось 
            // в памяти еще до возвращения ответа клиенту.
            UpdateNodeValue(targetVolumeNode, vol);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pipette]: Failed to parse the new Target Value: {ex.Message}");
            // Сообщаем клиенту об ошибке формата!
            return StatusCodes.BadTypeMismatch; 
        }

        return StatusCodes.Good;
    }

    /// <summary>
    /// Функция-обработчик методов (Aspirate, Dispense и тд). Вызывается, когда клиент отправляет "CallMethodRequest".
    /// </summary>
    private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
    {
        // Снова получаем динамический индекс нашего NameSpace
        ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://Pipette");
        
        // Правильный узел состояния (State Machine - Idle, Running, Complete) для FunctionalUnitState: 6036!
        var currentStateNode = FindPredefinedNode(new NodeId(6036u, ns), typeof(BaseVariableState)) as BaseVariableState;
        
        // 6092 - текущий актуальный объем (Current Value)
        var currentVolumeNode = FindPredefinedNode(new NodeId(6092u, ns), typeof(BaseVariableState)) as BaseVariableState;
        // 6093 - тот самый объем, который мы хотим набрать (Target Value)
        var targetVolumeNode = FindPredefinedNode(new NodeId(6093u, ns), typeof(BaseVariableState)) as BaseVariableState;
        // 6118 - наличие наконечника (TipController)
        var tipCurrentNode = FindPredefinedNode(new NodeId(6118u, ns), typeof(BaseVariableState)) as BaseVariableState;

        // Смотрим, какое состояние перед вызовом. Используем AS, чтобы безопасно преобразовать значение к типу LocalizedText
        var stateBefore = currentStateNode?.Value as Opc.Ua.LocalizedText;
        
        // Пишем в консоль, КАКОЙ конкретно метод вызвал клиент (method.BrowseName.Name хранит имя, например "Aspirate")
        Console.WriteLine($"[Pipette]: Method '{method.BrowseName.Name}' called. Current State: {stateBefore?.Text ?? "Unknown"}");

        // ПРОВЕРКА БЕЗОПАСНОСТИ (WORKFLOW)
        // Если вызывается Aspirate или Dispense, но нет носика (tipCurrentNode == false), просто предупредим (можно и запретить вызов)
        if ((method.BrowseName.Name == "Aspirate" || method.BrowseName.Name == "Dispense") && tipCurrentNode?.Value is false)
        {
            Console.WriteLine("[Pipette]: Warning! Trying to Aspirate/Dispense without a Tip attached!");
            // Примечание: если раскомментировать строку ниже, вызов метода прервется и вернет ошибку клиенту:
            // return StatusCodes.BadInvalidState;
        }

        // Вместо того, чтобы подвешивать сервер (блокировать поток), делегируем механическую работу пипетки в отдельный асинхронный टास्क
        StartDeviceTask(method.BrowseName.Name, currentStateNode, currentVolumeNode, targetVolumeNode, tipCurrentNode);

        // Печатаем, что задача успешно обработана и запущена в фоне
        Console.WriteLine($"[Pipette]: Command '{method.BrowseName.Name}' dispatched.");

        // Моментально отвечаем клиенту "Команда принята" (чтобы его подключение к серверу не зависло в режиме ожидания)
        return StatusCodes.Good;
    }

    /// <summary>
    /// Асинхронная задача (Task), которая симулирует долгое физическое действие устройства (набор жидкости / сброс).
    /// </summary>
    private void StartDeviceTask(string commandName, BaseVariableState currentStateNode, BaseVariableState currentVolumeNode, BaseVariableState targetVolumeNode, BaseVariableState tipCurrentNode)
    {
        // 1. Отменяем предыдущую задачу, если она еще идет. 
        // Например: пипетка работала, а мы резко послали новую команду. Механизм Cancellation Tokens убивает старый поток.
        _taskCts?.Cancel();
        
        // Создаем новый токен (билетик) для текущей задачи
        _taskCts = new CancellationTokenSource();
        var token = _taskCts.Token;

        // Task.Run отправляет кусок кода выполняться в параллельный поток компьютера
        Task.Run(async () =>
        {
            try
            {
                // ИЗМЕНЕНИЕ МАШИНЫ СОСТОЯНИЙ (State Machine) -> Меняем состояние устройства на "Running"
                // Это увидит любой клиент, который читает переменную CurrentState (id 6096)
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Running"));
                Console.WriteLine($"[Pipette]: Starting '{commandName}' operation... State Machine -> Running");
                
                // Читаем значение из Target Volume, которое мы хотим набрать, с безопасной конвертацией
                double targetVol = 0.0;
                if (targetVolumeNode?.Value != null) targetVol = Convert.ToDouble(targetVolumeNode.Value);
                
                // Читаем текущий актуальный объем в пипетке (чтобы знать, с какой точки начать набор или сброс)
                double currentVol = 0.0;
                if (currentVolumeNode?.Value != null) currentVol = Convert.ToDouble(currentVolumeNode.Value);

                // ВЕТВЛЕНИЕ ЛОГИКИ СЦЕНАРИЯ (WORKFLOW) В ЗАВИСИМОСТИ ОТ ВЫЗВАННОГО МЕТОДА
                if (commandName == "Aspirate") // ОПЕРАЦИЯ: ВТЯНУТЬ ЖИДКОСТЬ
                {
                    Console.WriteLine($"[Pipette]: Drawing fluid up to {targetVol} ml...");
                    
                    // Цикл while плавно заполняет объем от Current до Target. Это создает эффект живого движения поршня.
                    while (currentVol < targetVol)
                    {
                        currentVol += 1.0; // набираем по 1 миллилитру
                        // Защита от переполнения: если перескочили Target, обрезаем.
                        if (currentVol > targetVol) currentVol = targetVol;
                        
                        // Апдейтим переменную CurrentValue в OPC-сервере. Клиенты увидят, что число растет!
                        UpdateNodeValue(currentVolumeNode, currentVol); 
                        await Task.Delay(200, token); // Задержка 200мс (скорость механики аппарата)
                    }
                    Console.WriteLine($"[Pipette]: Aspiration finished.");
                }
                else if (commandName == "Dispense") // ОПЕРАЦИЯ: СБРОСИТЬ ЖИДКОСТЬ
                {
                    Console.WriteLine($"[Pipette]: Dispensing all fluid down to 0 ml...");
                    
                    // Цикл while плавно сливает объем, пока он не дойдет до полного нуля.
                    while (currentVol > 0.0)
                    {
                        currentVol -= 1.0; // сливаем по 1 миллилитру
                        if (currentVol < 0.0) currentVol = 0.0;
                        UpdateNodeValue(currentVolumeNode, currentVol); // Обновляем данные для HMI
                        await Task.Delay(200, token);
                    }
                    Console.WriteLine($"[Pipette]: Dispense complete. Auto-ejecting tip...");
                    
                    // АВТОМАТИЧЕСКАЯ ОТСТРЕЛКА НОСИКА ПОСЛЕ DISPENSE
                    // (Согласно требованиям сценария, устройство должно само снять носик после разлива жидкости)
                    await Task.Delay(500, token); // Небольшая задержка перед отстрелом (физика)
                    UpdateNodeValue(tipCurrentNode, false); // Меняем OPC-переменную наличия носика на false (снят)
                    Console.WriteLine($"[Pipette]: Tip ejected automatically. Waiting for AttachTip command from Client.");
                }
                else if (commandName == "AttachTip") // ОПЕРАЦИЯ: ОДЕТЬ НОСИК (Ручной вызов)
                {
                    Console.WriteLine($"[Pipette]: Attaching Tip...");
                    await Task.Delay(1000, token); // Симулируем работу мотора (1 секунда)
                    UpdateNodeValue(tipCurrentNode, true); // Передаем клиенту, что носик успешно захвачен (true)
                    Console.WriteLine($"[Pipette]: Tip Attached successfully.");
                }
                else if (commandName == "EjectTip") // ОПЕРАЦИЯ: СБРОСИТЬ НОСИК (Ручной вызов)
                {
                    Console.WriteLine($"[Pipette]: Ejecting Tip (Manual call)...");
                    await Task.Delay(1000, token); // Механика устройства работает...
                    UpdateNodeValue(tipCurrentNode, false); // Фиксируем, что носик пуст (false)
                    Console.WriteLine($"[Pipette]: Tip Ejected successfully.");
                }

                // Перед переходом в следующее состояние проверяем, не отменили ли аппаратную задачу извне?
                // Если отменили - функция выбросит ошибку OperationCanceledException и перепрыгнет в блок catch
                token.ThrowIfCancellationRequested();

                // ИЗМЕНЕНИЕ МАШИНЫ СОСТОЯНИЙ (State Machine) -> Меняем состояние на "Complete" (Завершен)
                // Действие успешно завершено, сообщаем об этом всем клиентам в сети
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                Console.WriteLine($"[Pipette]: '{commandName}' operation Complete. State Machine -> Complete");
                
                // Делаем финальную паузу в 1.5 секунды. Это нужно, чтобы клиент (или скрипт на питоне) успел 
                // считать состояние Complete и понять, что шаг Workflow пройден успешно, прежде чем вернуть в Idle.
                await Task.Delay(1500, token);
                
                // ИЗМЕНЕНИЕ МАШИНЫ СОСТОЯНИЙ (State Machine) -> Возвращаем устройство в состояние "Idle" (Ожидание)
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
                Console.WriteLine("[Pipette]: Ready for next command. State Machine -> Idle");
            }
            catch (OperationCanceledException)
            {
                // Сюда код прыгет, если мы отменили операцию (_taskCts.Cancel()). Информируем клиента о прерывании (Aborted).
                UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                Console.WriteLine($"[Pipette]: '{commandName}' operation was cancelled. State -> Aborted");
            }
            catch (Exception ex)
            {
                // Глобальный перехват любых иных сбоев (ошибка конвертации типа, пустое значение и тд)
                Console.WriteLine($"[Pipette]: Error: {ex.Message}");
            }
        }, token); // Передаем токен напрямую в асинхронный Task, чтобы он за ним следил
    }

    /// <summary>
    /// Вспомогательная функция для безопасного обновления переменных.
    /// Почему нельзя просто написать Node.Value = x?
    /// Потому что простая запись значения не уведомляет клиентов, которые подписаны на обновления (Subscriptions) OPC UA.
    /// </summary>
    private void UpdateNodeValue(BaseVariableState node, object newValue)
    {
        if (node != null) // Защита от NullReferenceException
        {
            // 1. Устанавливаем новое значение
            node.Value = newValue;
            
            // 2. Обновляем метку времени, когда именно произошло изменение (по Гринвичу, как того требует стандарт)
            node.Timestamp = DateTime.UtcNow;
            node.StatusCode = StatusCodes.Good;
            
            // 3. ВАЖНО: Обязательно нужно вызывать это, чтобы уведомить OPC UA Сервер 
            // и сбросить маски изменений, тогда подписчики получат DataChange Notification.
            node.ClearChangeMasks(SystemContext, false);
        }
    }
}









