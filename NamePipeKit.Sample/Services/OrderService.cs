using System;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Attributes;

namespace NamePipeKit.Sample.Services
{
    /// <summary>示例服务：订单相关能力。</summary>
    [PipeService]
    public sealed class OrderService
    {
        /// <summary>创建订单，返回订单号。</summary>
        [PipeMethod("order.create", Description = "创建订单")]
        public Task<string> CreateOrderAsync(string userId, decimal amount)
        {
            return Task.FromResult("ORD-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + userId + "-" + amount.ToString("F2"));
        }

        /// <summary>查询订单状态。</summary>
        [PipeMethod("order.status", Description = "查询订单状态")]
        public string GetStatus(string orderId)
        {
            return orderId != null && orderId.StartsWith("ORD-") ? "PAID" : "UNKNOWN";
        }

        /// <summary>计算总额。</summary>
        [PipeMethod("order.total")]
        public decimal CalcTotal(decimal[] prices)
        {
            decimal sum = 0;
            foreach (var p in prices) sum += p;
            return sum;
        }
    }

    /// <summary>示例服务：系统信息。</summary>
    [PipeService]
    public sealed class SystemService
    {
        [PipeMethod("system.ping")]
        public string Ping(string msg) { return "PONG: " + msg; }

        [PipeMethod("system.now")]
        public DateTime Now() { return DateTime.Now; }
    }
}