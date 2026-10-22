// ════════════════════════════════════════════════════════════════════════════
//  文件：PipeMethodRegistry.cs
//  作用：扫描所有 [PipeService]+[PipeMethod] 并注册到方法表。
//  启动时调用一次 ScanAssemblies，之后 TryGet 就是纯字典查找。
//  线程安全：ConcurrentDictionary。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Attributes;
using NamePipeKit.Abstractions.Logging;

namespace NamePipeKit.Discovery
{
    /// <summary>一个被注册的管道方法描述符。</summary>
    public sealed class PipeMethodDescriptor
    {
        /// <summary>客户端调用时使用的 Key。</summary>
        public string Key { get; private set; }

        /// <summary>声明该方法的服务类型（用于实例化）。</summary>
        public Type ServiceType { get; private set; }

        /// <summary>方法反射信息。</summary>
        public MethodInfo Method { get; private set; }

        /// <summary>预编译的调用器（避免每次 MethodInfo.Invoke）。</summary>
        public Func<object, object[], object> Invoker { get; private set; }

        /// <summary>方法是否返回 Task / Task&lt;T&gt;（需要 await）。</summary>
        public bool IsAsync
        {
            get { return typeof(Task).IsAssignableFrom(Method.ReturnType); }
        }

        public PipeMethodDescriptor(string key, Type serviceType, MethodInfo method,
            Func<object, object[], object> invoker)
        {
            Key = key;
            ServiceType = serviceType;
            Method = method;
            Invoker = invoker;
        }
    }

    /// <summary>管道方法注册表。</summary>
    public sealed class PipeMethodRegistry
    {
        private readonly ConcurrentDictionary<string, PipeMethodDescriptor> _methods =
            new ConcurrentDictionary<string, PipeMethodDescriptor>(StringComparer.OrdinalIgnoreCase);
        private readonly IPipeLogger _logger;

        public PipeMethodRegistry(IPipeLogger logger) { _logger = logger; }

        /// <summary>已注册方法数量。</summary>
        public int Count { get { return _methods.Count; } }

        /// <summary>扫描多个程序集，注册所有 [PipeService]+[PipeMethod] 方法。</summary>
        public void ScanAssemblies(IEnumerable<Assembly> assemblies)
        {
            foreach (var assembly in assemblies.Distinct())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    _logger.Warn("[Registry] 加载类型失败：" + assembly.FullName, ex);
                    continue;
                }

                foreach (var type in types.Where(t => t.IsClass && !t.IsAbstract))
                {
                    // 只处理带 [PipeService] 的类
                    if (type.GetCustomAttribute<PipeServiceAttribute>() == null) continue;

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var methodAttr = method.GetCustomAttribute<PipeMethodAttribute>();
                        if (methodAttr == null) continue;

                        // Key 优先用特性指定的，否则用 "类名.方法名"
                        var key = string.IsNullOrEmpty(methodAttr.Key)
                            ? type.Name + "." + method.Name
                            : methodAttr.Key;

                        var descriptor = new PipeMethodDescriptor(
                            key, type, method, PipeMethodInvokerFactory.Create(method));

                        if (_methods.TryAdd(key, descriptor))
                            _logger.Debug("[Registry] 注册：" + key + " → " + type.Name + "." + method.Name);
                        else
                            _logger.Warn("[Registry] Key 冲突，忽略：" + key);
                    }
                }
            }

            _logger.Info("[Registry] 共注册 " + _methods.Count + " 个管道方法");
        }

        /// <summary>按 Key 查找方法描述符。</summary>
        public bool TryGet(string key, out PipeMethodDescriptor descriptor)
        {
            return _methods.TryGetValue(key, out descriptor);
        }

        /// <summary>获取所有可用方法 Key（用于错误提示）。</summary>
        public IEnumerable<string> GetAllKeys() { return _methods.Keys; }
    }
}