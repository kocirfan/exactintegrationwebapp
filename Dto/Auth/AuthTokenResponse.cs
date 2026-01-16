using System;

namespace ShopifyProductApp.Dto.Auth
{
    public class AuthTokenResponse
    {
        public string Token { get; set; }
        public DateTime Expiration { get; set; }
        public string Username { get; set; }
    }
}
