// ════════════════════════════════════════════════════════════════════════════
//  文件：OrderService.cs
//  作用：业务服务示例。演示 [PipeService] + [PipeMethod] 的用法。
//  扩展方式：想加新业务方法，只需在此类里加 [PipeMethod] 方法，
//           或者新建一个 [PipeService] 类——框架自动扫描注册。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Attributes;

namespace NamePipeKit.ServerSample.Services
{
    /// <summary>订单服务。</summary>
    [PipeService]
    public sealed class OrderService
    {
        /// <summary>创建订单（异步示例）。</summary>
        [PipeMethod("order.create")]
        public Task<string> CreateOrderAsync(string userId, decimal amount)
        {
            return Task.FromResult("ORD-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")
                + "-" + userId + "-" + amount.ToString("F2"));
        }

        /// <summary>查询订单状态。</summary>
        [PipeMethod("order.status")]
        public string GetStatus(string orderId)
        {
            return orderId != null && orderId.StartsWith("ORD-") ? "PAID" : "UNKNOWN";
        }

        /// <summary>数组参数示例。</summary>
        [PipeMethod("order.total")]
        public decimal CalcTotal(decimal[] prices)
        {
            decimal sum = 0;
            if (prices != null) foreach (var p in prices) sum += p;
            return sum;
        }
    }
}