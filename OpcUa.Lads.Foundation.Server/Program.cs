using Opc.Ua.Configuration;
using Opc.Ua;
using Serilog;
using System.Net;
using OpcUa.Lads.Foundation.Server;

// 1. Создаем экземпляры приложения и сервера
ApplicationInstance application;
Server server;

// 2. Настраиваем логирование через Serilog (вывод в консоль)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

// 3. Загружаем и валидируем настройки сервера, порты и сертификаты безопасности
Configure("testPipette", 62451);

Log.Information("Starting OPC-UA server (press enter to stop)");
// 4. Запускаем сервер: инициализируем NodeManager-ы, открываем порт и ждем клиентов
Start();

// Сервер запущен в фоновом потоке, поэтому просто ждем нажатия Enter в консоли
Console.ReadLine();

Log.Information("Shutting down...");
// 5. Корректно завершаем работу (отключаем всех клиентов)
Stop();


void Configure(string applicationName, int port)
{
    // Создаем параметры конфигурации сервера: название, URI, порты, сертификаты безопасности
    var configuration = new ApplicationConfiguration
    {
        ApplicationName = applicationName,
        ApplicationUri = $"urn:{Dns.GetHostName()}:{applicationName}",
        ProductUri = $"uri:opcfoundation.org:{applicationName}",
        ApplicationType = ApplicationType.Server,

        SecurityConfiguration =
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = "Directory",
                StorePath = "%LocalApplicationData%/OPC Foundation/pki/own",
                SubjectName = $"CN={applicationName}, C=US, S=Arizona, O=OPC Foundation, DC={Dns.GetHostName()}"
            },
            TrustedIssuerCertificates = new CertificateTrustList
            {
                StoreType = "Directory",
                StorePath = "%LocalApplicationData%/OPC Foundation/pki/issuer"
            },
            TrustedPeerCertificates = new CertificateTrustList
            {
                StoreType = "Directory",
                StorePath = "%LocalApplicationData%/OPC Foundation/pki/trusted"
            },
            RejectedCertificateStore = new CertificateStoreIdentifier
            {
                StoreType = "Directory",
                StorePath = "%LocalApplicationData%/OPC Foundation/pki/rejected"
            },
            AutoAcceptUntrustedCertificates = false,
            RejectSHA1SignedCertificates = true,
            RejectUnknownRevocationStatus = true,
            MinimumCertificateKeySize = 2048,
            AddAppCertToTrustedStore = false,
            SendCertificateChain = true
        },
        // Конфигурируем сам HTTP/TCP сервер и политики безопасности (какие клиенты могут подключаться)
        ServerConfiguration = new ServerConfiguration
        {
            BaseAddresses =
            [
                $"opc.tcp://{Dns.GetHostName()}:{port}/{applicationName}"
            ],
            SecurityPolicies =
            [
                new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = "http://opcfoundation.org/UA/SecurityPolicy#None" },
                new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = "" },
                new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256" }
            ],
            UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
            MaxRegistrationInterval = 0,
            DiagnosticsEnabled = true
        },
        TransportConfigurations = [],
        TransportQuotas = new TransportQuotas() // Настройки лимитов по TCP (максимальный размер сообщения и т.д.)
    };

    // Валидируем конфигурацию сервера
    configuration.Validate(ApplicationType.Server).Wait();

    // Создаем экземпляр ApplicationInstance. 
    // Это оболочка, которая отвечает за жизненный цикл OPC UA приложения и управление сертификатами.
    application = new ApplicationInstance(configuration)
    {
        ApplicationName = applicationName,
        ApplicationType = ApplicationType.Server,
        CertificatePasswordProvider = new CertificatePasswordProvider("")
    };

    // Проверяем, существует ли у сервера сертификат (если нет - он может быть создан автоматически)
    application.CheckApplicationInstanceCertificate(false, CertificateFactory.DefaultKeySize).Wait();
}

void Start()
{
    // Создаем ядро OPC UA сервера
    server = new Server();
    
    // ДОБАВЛЯЕМ NODE MANAGER. 
    // NodeManager — это "менеджер адресного пространства". Мы передаем ему нашу фабрику (PipetteNodeManagerFactory).
    // Из-за этого при старте сервер вызовет метод CreateAddressSpace, распарсит Pipette.xml и опубликует пипетку в сеть.
    server.AddNodeManager(new PipetteNodeManagerFactory());
    
    // Запуск сервера. В этот момент открываются сетевые TCP сокеты.
    try
    {
        application.Start(server).Wait();

        // Подписываемся на события: просто пишем в лог, когда клиент подключается или отключается.
        server.CurrentInstance.SessionManager.SessionActivated += (session, reason) => Log.Information($"Session {session.Id} ({session.Identity.DisplayName}) : {reason}");
        server.CurrentInstance.SessionManager.SessionClosing+= (session, reason) => Log.Information($"Session {session.Id} ({session.Identity.DisplayName}) : {reason}");
        server.CurrentInstance.SessionManager.SessionCreated += (session, reason) => Log.Information($"Session {session.Id} ({session.Identity.DisplayName}) : {reason}");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Unable to start server!");
    }

    // Print endpoint info
    foreach (var endpoint in application.Server.GetEndpoints().Select(e => e.EndpointUrl).Distinct())
    {
        Log.Information(endpoint);
    }
}

void Stop()
{
    server.Stop();
}
