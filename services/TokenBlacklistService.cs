using Microsoft.Extensions.Caching.Memory;

namespace ShopifyProductApp.Services
{
    public interface ITokenBlacklistService
    {
        Task BlacklistTokenAsync(string jti, DateTime expirationDate);
        Task<bool> IsTokenBlacklistedAsync(string jti);
    }

    public class TokenBlacklistService : ITokenBlacklistService
    {
        private readonly IMemoryCache _cache;

        public TokenBlacklistService(IMemoryCache cache)
        {
            _cache = cache;
        }

        public Task BlacklistTokenAsync(string jti, DateTime expirationDate)
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expirationDate
            };

            _cache.Set(jti, true, options);

            return Task.CompletedTask;
        }

        public Task<bool> IsTokenBlacklistedAsync(string jti)
        {
            return Task.FromResult(_cache.TryGetValue(jti, out _));
        }
    }
}
