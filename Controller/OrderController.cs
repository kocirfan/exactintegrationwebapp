using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;
using Microsoft.AspNetCore.Cors; // Bunu ekleyin
using Newtonsoft.Json;
using System.Text;
using ExactOnline.Models;
using Microsoft.AspNetCore.Authorization;

namespace ShopifyProductApp.Controllers

{
    // [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    // [EnableCors("AllowAll")] // CORS'u etkinleştir
    public class OrderController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ShopifyService _shopifyService;
        private readonly ShopifyGraphQLService _graphqlService;
        private readonly AppConfiguration _config;

        private readonly ShopifyOrderCrud _shopifyOrderCrud;
        private readonly ILogger<ProductsController> _logger;
        private readonly IConfiguration _configg;


        public OrderController(ShopifyGraphQLService graphqlService, ExactService exactService, ShopifyService shopifyService, AppConfiguration config, ShopifyOrderCrud shopifyOrderCrud, ILogger<ProductsController> logger, IConfiguration configg)
        {
            _graphqlService = graphqlService;
            _exactService = exactService;
            _shopifyService = shopifyService;
            _config = config;
            _shopifyOrderCrud = shopifyOrderCrud;
            _logger = logger;
            _configg = configg;
        }



        [HttpGet("exact-orders-by-email/{email}")]
        public async Task<IActionResult> GetOrdersByEmail(string email, [FromQuery] int top = 100, [FromQuery] int skip = 0)
        {
            if (string.IsNullOrEmpty(email))
            {
                return BadRequest(new { error = "Email parametresi gerekli" });
            }

            try
            {
                var orders = await _exactService.GetOrdersByCustomerEmail(email, top, skip);

                if (orders == null || !orders.Any())
                {
                    return NotFound(new { message = $"Email '{email}' için sipariş bulunamadı" });
                }

                return Ok(new
                {
                    email = email,
                    totalOrders = orders.Count,
                    orders = orders // SalesOrderLines ile birlikte gelecek
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }



        [HttpGet("exact-orders/{customerGuid}")]
        public async Task<IActionResult> GetOrdersByCustomer(Guid customerGuid, [FromQuery] int top = 100, [FromQuery] int skip = 0)
        {
            try
            {
                var orders = await _exactService.GetOrdersByCustomerGuid(customerGuid, top, skip);

                if (orders == null || !orders.Any())
                {
                    return NotFound(new { message = $"Müşteri {customerGuid} için sipariş bulunamadı" });
                }

                return Ok(new
                {
                    customerGuid = customerGuid,
                    totalOrders = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("exact-order-detail/{orderId}")]
        public async Task<IActionResult> GetOrderDetail(Guid orderId)
        {
            try
            {
                var orderDetail = await _exactService.GetOrderDetailByOrderId(orderId);

                if (orderDetail == null)
                {
                    return NotFound(new { message = $"Sipariş {orderId} bulunamadı" });
                }

                return Ok(orderDetail);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("exact-salesorder")]
        public async Task<IActionResult> GetAllSalesOrder()
        {
            var customersJson = await _exactService.GetAlSalesOrderAsync();
            return Content(customersJson, "application/json"); // Raw JSON döndür
        }



        [HttpGet("shopify-order/{orderId}")]
        public async Task<IActionResult> GetShopifyOrderById(long orderId)
        {
            
            var order = await _shopifyOrderCrud.GetOrderByIdAsync(orderId);
            if (order == null)
            {
                return NotFound(new { message = $"Shopify sipariş {orderId} bulunamadı" });
            }
            return Ok(order);
        }

          [HttpGet("shopifyorder/{orderId}")]
        public async Task<IActionResult> GetJustShopifyOrderById(long orderId)
        {
            
            var order = await _shopifyOrderCrud.JustGetOrderByIdAsync(orderId);
            if (order == null)
            {
                return NotFound(new { message = $"Shopify sipariş {orderId} bulunamadı" });
            }
            return Ok(order);
        }
    }
}