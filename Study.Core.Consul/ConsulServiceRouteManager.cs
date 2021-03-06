﻿using Study.Core.ServiceDiscovery.Imp;
using System;
using System.Collections.Generic;
using System.Text;
using Study.Core.ServiceDiscovery;
using System.Threading.Tasks;
using Study.Core.Serialization;
using Consul;
using Study.Core.Consul.Configuration;
using Microsoft.Extensions.Options;
using Study.Core.Address;
using Microsoft.Extensions.Logging;
using Study.Core.Consul.WatcherProvider;
using System.Linq;
using Study.Core.Consul.Utilitys;
using Study.Core.ServiceDiscovery.RouteEventArgs;
using Study.Core.Runtime.Server.Configuration;

namespace Study.Core.Consul
{
    public class ConsulServiceRouteManager : ServiceRouteManagerBase, IDisposable
    {
        private readonly ConsulClient _consul;
        private readonly ConfigInfo _config;
        private readonly ServerAddress _address;
        private readonly IClientWatchManager _manager;
        private readonly ISerializer<byte[]> _byteSerializer;
        private readonly ISerializer<string> _stringSerializer;
        private readonly ILogger<ConsulServiceRouteManager> _logger;
        private ServiceRoute[] _routes;
        private readonly IServiceRouteFactory _serviceRouteFactory;

        public ConsulServiceRouteManager(
            ISerializer<string> serializer
            , ISerializer<byte[]> byteSerializer
            , ISerializer<string> stringSerializer
            , IOptions<ConfigInfo> configInfo
            , IOptions<ServerAddress> address
            , IClientWatchManager manager
            , IServiceRouteFactory serviceRouteFactory
            , ILogger<ConsulServiceRouteManager> logger) : base(serializer)
        {
            _config = configInfo.Value;
            _address = address.Value;
            _manager = manager;
            _byteSerializer = byteSerializer;
            _stringSerializer = stringSerializer;
            _consul = new ConsulClient(config =>
            {
                config.Address = new Uri($"http://{_config.Host}:{_config.Port}");
            });
            _serviceRouteFactory = serviceRouteFactory;
            _logger = logger;
            EnterRoutes().Wait();
        }

        public override async Task Register(IEnumerable<ServiceRoute> routes)
        {
            var host = new IpAddressModel(_address.Host, _address.Port) as AddressModel;

            var serviceRoutes = await GetRoutes(routes.Select(p => $"{ _config.RoutePath}{p.ServiceDescriptor.Id}"));
            var arrRoutes = routes.ToArray();
            var cnt = routes.Count();
            for (var i = 0; i < cnt; i++)
            {
                var route = arrRoutes[i];
                var serviceRoute = serviceRoutes.Where(p => p.ServiceDescriptor.Id == route.ServiceDescriptor.Id).FirstOrDefault();

                if (serviceRoute != null)
                {
                    var addresses = serviceRoute.Address.Concat(
                      route.Address.Except(serviceRoute.Address)).ToList();

                    foreach (var address in route.Address)
                    {
                        addresses.Remove(addresses.Where(p => p.ToString() == address.ToString()).FirstOrDefault());
                        addresses.Add(address);
                    }
                    route.Address = addresses;
                }
                arrRoutes[i] = route;
            }
            #region MyRegion
            //foreach (var route in routes)
            //{
            //    var serviceRoute = serviceRoutes.Where(p => p.ServiceDescriptor.Id == route.ServiceDescriptor.Id).FirstOrDefault();

            //    if (serviceRoute != null)
            //    {
            //        var addresses = serviceRoute.Address.Concat(
            //          route.Address.Except(serviceRoute.Address)).ToList();

            //        foreach (var address in route.Address)
            //        {
            //            addresses.Remove(addresses.Where(p => p.ToString() == address.ToString()).FirstOrDefault());
            //            addresses.Add(address);
            //        }
            //        route.Address = addresses;
            //    }
            //}
            #endregion
            await RemoveExceptRoutesAsync(arrRoutes, host);

            await base.Register(arrRoutes);
        }


        public override async Task DeregisterAsync()
        {
            var host = new IpAddressModel(_address.Host, _address.Port) as AddressModel;



            if (_consul.KV.Keys(_config.RoutePath).Result.Response?.Count() > 0)
            {
                var keys = await _consul.KV.Keys(_config.RoutePath);
                var routes = await GetRoutes(keys.Response);
                await DeRegisterRoutesAsync(routes, host);
            }


            //var queryResult = await _consul.KV.List(_config.RoutePath);
            //var response = queryResult.Response;
            //if (response != null)
            //{
            //    foreach (var result in response)
            //    {
            //        await _consul.KV.DeleteCAS(result);
            //    }
            //}
        }

        public void Dispose()
        {
            _consul.Dispose();
        }

        public override async Task<IEnumerable<ServiceRoute>> GetRoutesAsync()
        {
            await EnterRoutes();
            return _routes;
        }

        public override async Task SetRoutesAsync(IEnumerable<ServiceRouteDescriptor> descriptors)
        {
            foreach (var d in descriptors)
            {
                var nodeData = _byteSerializer.Serialize(d);
                var keyValuePair = new KVPair($"{_config.RoutePath}{d.ServiceDescriptor.Id}") { Value = nodeData };
                await _consul.KV.Put(keyValuePair);
            }
        }

        private async Task<ServiceRoute> GetRouteData(string data)
        {
            if (data == null)
                return null;
            var descriptor = _stringSerializer.Deserialize<string, ServiceRouteDescriptor>(data);

            return (await _serviceRouteFactory.CreateServiceRoutesAsync(new[] { descriptor })).First();
        }

        private async Task EnterRoutes()
        {
            if (_routes != null && _routes.Length > 0)
                return;

            var watcher = new ChildrenMonitorWatcher(_manager, _consul
                , async (oldChildrens, newChildrens) => await ChildrenChange(oldChildrens, newChildrens), (result) => ConvertPaths(result).Result, _config.RoutePath);
            if (_consul.KV.Keys(_config.RoutePath).Result.Response?.Count() > 0)
            {
                var result = await _consul.GetChildrenAsync(_config.RoutePath);
                var keys = await _consul.KV.Keys(_config.RoutePath);
                var childrens = result;
                watcher.SetCurrentData(ConvertPaths(childrens).Result.Select(key => $"{_config.RoutePath}{key}").ToArray());
                _routes = await GetRoutes(keys.Response);
            }
            else
            {
                if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning))
                    _logger.LogWarning($"无法获取路由信息，因为节点：{_config.RoutePath}，不存在。");
                _routes = new ServiceRoute[0];
            }
        }

        /// <summary>
        /// 转化路径集合
        /// </summary>
        /// <param name="datas">信息数据集合</param>
        /// <returns>返回路径集合</returns>
        private async Task<string[]> ConvertPaths(string[] datas)
        {
            List<string> paths = new List<string>();
            foreach (var data in datas)
            {
                var result = await GetRouteData(data);
                var serviceId = result?.ServiceDescriptor.Id;
                if (!string.IsNullOrEmpty(serviceId))
                    paths.Add(serviceId);
            }
            return paths.ToArray();
        }

        private async Task ChildrenChange(string[] oldChildrens, string[] newChildrens)
        {
            if (oldChildrens == null && newChildrens == null)
                return;

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.LogDebug($"最新的节点信息：{string.Join(",", newChildrens)}");

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.LogDebug($"旧的节点信息：{string.Join(",", oldChildrens)}");

            //计算出已被删除的节点。
            var deletedChildrens = oldChildrens.Except(newChildrens).ToArray();
            //计算出新增的节点。
            var createdChildrens = newChildrens.Except(oldChildrens).ToArray();

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.LogDebug($"需要被删除的路由节点：{string.Join(",", deletedChildrens)}");
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.LogDebug($"需要被添加的路由节点：{string.Join(",", createdChildrens)}");

            //获取新增的路由信息。
            var newRoutes = (await GetRoutes(createdChildrens)).ToArray();

            var routes = _routes.ToArray();

            lock (_routes)
            {
                _routes = _routes
                     //删除无效的节点路由。
                     .Where(i => !deletedChildrens.Contains($"{_config.RoutePath}{i.ServiceDescriptor.Id}"))
                     //连接上新的路由。
                     .Concat(newRoutes)
                     .ToArray();
            }

            //需要删除的路由集合。
            var deletedRoutes = routes.Where(i => deletedChildrens.Contains($"{_config.RoutePath}{i.ServiceDescriptor.Id}")).ToArray();
            //触发删除事件。
            OnRemoved(deletedRoutes.Select(route => new ServiceRouteEventArgs(route)).ToArray());

            //触发路由被创建事件。
            OnCreated(newRoutes.Select(route => new ServiceRouteEventArgs(route)).ToArray());

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
                _logger.LogInformation("路由数据更新成功。");
        }

        private async Task<ServiceRoute[]> GetRoutes(IEnumerable<string> childrens)
        {
            childrens = childrens.ToArray();
            var routes = new List<ServiceRoute>(childrens.Count());
            foreach (var children in childrens)
            {
                if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                    _logger.LogDebug($"准备从节点：{children}中获取路由信息。");

                var route = await GetRoute(children);
                if (route != null)
                    routes.Add(route);
            }

            return routes.ToArray();
        }

        private async Task RemoveExceptRoutesAsync(IEnumerable<ServiceRoute> routes, AddressModel hostAddr)
        {
            routes = routes.ToArray();

            if (_routes != null)
            {
                var oldRouteIds = _routes.Select(i => i.ServiceDescriptor.Id).ToArray();
                var newRouteIds = routes.Select(i => i.ServiceDescriptor.Id).ToArray();
                var deletedRouteIds = oldRouteIds.Except(newRouteIds).ToArray();
                foreach (var deletedRouteId in deletedRouteIds)
                {
                    var addresses = _routes.Where(p => p.ServiceDescriptor.Id == deletedRouteId).Select(p => p.Address).FirstOrDefault();
                    if (addresses.Contains(hostAddr))
                        await _consul.KV.Delete($"{_config.RoutePath}{deletedRouteId}");
                }
            }
        }
        /// <summary>
        /// 取消当前主机路由地址
        /// </summary>
        /// <param name="routes"></param>
        /// <param name="hostAddr"></param>
        /// <returns></returns>
        private async Task DeRegisterRoutesAsync(IEnumerable<ServiceRoute> routes, AddressModel hostAddr)
        {
            var arrRoutes = routes.ToList();
            if (_routes != null)
            {
                var cnt = routes.Count();
                for (var i = 0; i < cnt; i++)
                {
                    var route = arrRoutes[i];
                    var addresses = route.Address.ToList();
                    if (addresses.Contains(hostAddr))
                    {
                        addresses.Remove(hostAddr);
                        if (addresses.Count() == 0)
                        {
                            arrRoutes.Remove(route);
                            await _consul.KV.Delete($"{_config.RoutePath}{route.ServiceDescriptor.Id}");
                        }
                        else
                        {
                            route.Address = addresses;
                            arrRoutes[i] = route;
                        }
                    }
                }
                //重新注册路由
                await base.Register(arrRoutes);
            }
        }

        private async Task<ServiceRoute> GetRoute(string path)
        {
            ServiceRoute route = null;
            var watcher = new NodeMonitorWatcher(_consul, _manager, path, async (oldData, newData) => await NodeChange(oldData, newData));
            var queryResult = await _consul.KV.Keys(path);
            if (queryResult.Response != null)
            {
                var data = await _consul.GetDataAsync(path);
                if (data != null)
                {
                    watcher.SetCurrentData(data);
                    route = await GetRoute(data);
                }
            }

            return route;
        }

        private async Task<ServiceRoute> GetRoute(byte[] data)
        {
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.LogDebug($"准备转换服务路由，配置内容：{Encoding.UTF8.GetString(data)}。");
            if (data == null)
                return null;
            var descriptor = _byteSerializer.Deserialize<byte[], ServiceRouteDescriptor>(data);
            return (await _serviceRouteFactory.CreateServiceRoutesAsync(new[] { descriptor })).First();
        }


        private async Task NodeChange(byte[] oldData, byte[] newData)
        {
            if (DataEquals(oldData, newData))
                return;
            var newRoute = await GetRoute(newData);
            var oldRoute = _routes.FirstOrDefault(i => i.ServiceDescriptor.Id == newRoute.ServiceDescriptor.Id);

            lock (_routes)
            {
                _routes = _routes.Where(i => i.ServiceDescriptor.Id != newRoute.ServiceDescriptor.Id).Concat(new[] { newRoute }).ToArray();
            }

            //触发路由变更事件。
            OnChanged(new ServiceRouteChangedEventArgs(newRoute, oldRoute));
        }



        private static bool DataEquals(IReadOnlyList<byte> data1, IReadOnlyList<byte> data2)
        {
            if (data1.Count != data2.Count)
                return false;
            for (var i = 0; i < data1.Count; i++)
            {
                var b1 = data1[i];
                var b2 = data2[i];
                if (b1 != b2)
                    return false;
            }
            return true;
        }
    }
}
