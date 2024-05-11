using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework
{
    public sealed class ApplicationContext
    {
        private static ApplicationContext _instance;
        private static readonly object _lock = new object();

        private IComponentContext? _componentContext = null;
        private ILoggerFactory? _loggerFactory = null;
        private IConfiguration? _configuration = null;

        public IComponentContext? ComponentContext { get; private set; }

        public IConfiguration? Configuration { get; private set; }

        public ILoggerFactory? LoggerFactory { get; private set; }
        public ILogger<T>? CreateLogger<T>() => LoggerFactory?.CreateLogger<T>();
        public ILogger? CreateLogger(string categoryName) => LoggerFactory?.CreateLogger(categoryName);

        // Private constructor to prevent instantiation from outside
        private ApplicationContext()
        {
        }

        public void Initialize(IComponentContext? componentContext)
        {
            lock (_lock)
            {
                _instance._componentContext = componentContext;

                _instance._loggerFactory = componentContext?.Resolve<ILoggerFactory>();
                _instance._configuration = componentContext?.Resolve<IConfiguration>();
            }
        }

        // Public static method to get the instance
        public static ApplicationContext GetInstance()
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new ApplicationContext();
                    }
                }
            }

            return _instance;
        }
    }
}

