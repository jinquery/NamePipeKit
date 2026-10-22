// ════════════════════════════════════════════════════════════════════════════
//  文件：CallMethodHandler.cs
//  作用：处理 ContentType = "CALLMETHOD"。
//  流程：解析方法名 → 找方法描述符 → 创建/获取服务实例 → 绑定参数 → 调用 → 返回结果。
//  优化：
//    - 服务实例缓存（每类型只创建一次）；
//    - 常见值类型参数直取 JsonElement.GetXxx()，避免 GetRawText + 再反序列化。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Discovery;

namespace NamePipeKit.Handling
{
    /// <summary>CALLMETHOD 类型的处理器。</summary>
    public sealed class CallMethodHandler : IPipeRequestHandler
    {
        private readonly PipeMethodRegistry _registry;
        private readonly IPipeLogger _logger;

        /// <summary>服务实例缓存：每个 [PipeService] 类型只创建一次（要求服务无状态）。</summary>
        private readonly ConcurrentDictionary<Type, object> _serviceCache =
            new ConcurrentDictionary<Type, object>();

        public CallMethodHandler(PipeMethodRegistry registry, IPipeLogger logger)
        {
            _registry = registry;
            _logger = logger;
        }

        public string ContentType { get { return PipeContentTypes.CallMethod; } }

        public async Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(request.MethodName))
                return PipeResponse.Fail(PipeStatusCode.BadRequest, "缺少 MethodName。");

            PipeMethodDescriptor descriptor;
            if (!_registry.TryGet(request.MethodName, out descriptor))
            {
                var keys = string.Join(", ", _registry.GetAllKeys().Take(20));
                return PipeResponse.Fail(PipeStatusCode.MethodNotFound,
                    "找不到管道方法：" + request.MethodName + "。可用方法：" + keys);
            }

            // ── 获取（或首次创建）服务实例 ──
            object service;
            if (!_serviceCache.TryGetValue(descriptor.ServiceType, out service))
            {
                try
                {
                    service = ctx.ServiceFactory(descriptor.ServiceType);
                    if (service == null)
                        return PipeResponse.Fail(PipeStatusCode.ServerError,
                            "服务实例为空：" + descriptor.ServiceType);
                    _serviceCache.TryAdd(descriptor.ServiceType, service);
                }
                catch (Exception ex)
                {
                    _logger.Error("实例化服务失败：" + descriptor.ServiceType, ex);
                    return PipeResponse.Fail(PipeStatusCode.ServerError,
                        "服务实例化失败：" + ex.Message);
                }
            }

            // ── 参数绑定 ──
            object[] boundArgs;
            try { boundArgs = BindArguments(request.Args, descriptor.Method); }
            catch (Exception ex)
            {
                return PipeResponse.Fail(PipeStatusCode.ArgBindFailed, "参数绑定失败：" + ex.Message);
            }

            // ── 调用 ──
            try
            {
                var ret = descriptor.Invoker(service, boundArgs);

                // 如果返回 Task/Task<T>，需要等待完成并取 Result
                if (descriptor.IsAsync && ret is Task)
                {
                    var task = (Task)ret;
                    await task.ConfigureAwait(false);
                    var resultProp = ret.GetType().GetProperty("Result");
                    ret = resultProp != null ? resultProp.GetValue(ret) : null;
                }

                return PipeResponse.Ok(ret);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException != null ? tie.InnerException : tie;
                _logger.Error("调用 " + descriptor.Key + " 失败", inner);
                return PipeResponse.Fail(PipeStatusCode.InvokeFailed, inner.Message);
            }
            catch (Exception ex)
            {
                _logger.Error("调用 " + descriptor.Key + " 失败", ex);
                return PipeResponse.Fail(PipeStatusCode.InvokeFailed, ex.Message);
            }
        }

        /// <summary>将 JsonElement[] 绑定为方法参数强类型数组。</summary>
        private static object[] BindArguments(object[] rawArgs, MethodInfo method)
        {
            var parameters = method.GetParameters();
            var result = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (rawArgs == null || i >= rawArgs.Length)
                {
                    if (p.HasDefaultValue) { result[i] = p.DefaultValue; continue; }
                    throw new ArgumentException("缺少参数：" + p.Name);
                }

                var raw = rawArgs[i];
                if (raw is JsonElement)
                    result[i] = BindJsonElement((JsonElement)raw, p.ParameterType);
                else if (raw == null || p.ParameterType.IsInstanceOfType(raw))
                    result[i] = raw;
                else
                    result[i] = Convert.ChangeType(raw, p.ParameterType);
            }
            return result;
        }

        /// <summary>常见类型快路径绑定；复杂类型走标准反序列化。</summary>
        private static object BindJsonElement(JsonElement je, Type targetType)
        {
            if (je.ValueKind == JsonValueKind.Null) return null;

            if (targetType == typeof(string))
                return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
            if (targetType == typeof(int)) return je.GetInt32();
            if (targetType == typeof(long)) return je.GetInt64();
            if (targetType == typeof(double)) return je.GetDouble();
            if (targetType == typeof(float)) return (float)je.GetDouble();
            if (targetType == typeof(decimal)) return je.GetDecimal();
            if (targetType == typeof(bool)) return je.GetBoolean();
            if (targetType == typeof(DateTime)) return je.GetDateTime();
            if (targetType == typeof(Guid))
            {
                var s = je.GetString();
                return s != null ? Guid.Parse(s) : Guid.Empty;
            }

            // 数组、自定义对象等走标准反序列化
            return je.Deserialize(targetType);
        }
    }
}