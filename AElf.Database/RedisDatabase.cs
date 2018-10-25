﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Configuration;
using NServiceKit.Redis;

namespace AElf.Database
{
    public class RedisDatabase : IKeyValueDatabase
    {
        private readonly PooledRedisClientManager _client;

        public RedisDatabase()
        {
            _client = new PooledRedisClientManager(DatabaseConfig.Instance.Number,
                $"{DatabaseConfig.Instance.Host}:{DatabaseConfig.Instance.Port}");
        }

        public async Task<byte[]> GetAsync(string key, Type type)
        {
            return await Task.FromResult(_client.GetCacheClient().Get<byte[]>(key));
        }

        public async Task SetAsync(string key, byte[] bytes)
        {
            await Task.FromResult(_client.GetCacheClient().Set(key, bytes));
        }

        public async Task RemoveAsync(string key)
        {
            await Task.FromResult(_client.GetCacheClient());
        }

        public async Task<bool> PipelineSetAsync(Dictionary<string, byte[]> cache)
        {
            return await Task.Factory.StartNew(() =>
            {
                _client.GetCacheClient().SetAll(cache);
                return true;
            });
        }

        public bool IsConnected()
        {
            try
            {
                _client.GetCacheClient().Set<byte[]>("ping", null);
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }
    }
}