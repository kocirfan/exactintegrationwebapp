using System.Net;
using System.Security.Claims;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Middleware
{
    public class TokenBlacklistMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IServiceProvider _serviceProvider;

        public TokenBlacklistMiddleware(RequestDelegate next, IServiceProvider serviceProvider)
        {
            _next = next;
            _serviceProvider = serviceProvider;
        }

        public async Task Invoke(HttpContext context)
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var jti = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
                if (!string.IsNullOrEmpty(jti))
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var blacklistService = scope.ServiceProvider.GetRequiredService<ITokenBlacklistService>();
                        if (await blacklistService.IsTokenBlacklistedAsync(jti))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                            return;
                        }
                    }
                }
            }

            await _next(context);
        }
    }
}
