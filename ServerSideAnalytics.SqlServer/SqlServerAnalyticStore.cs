﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutoMapper;
using Maddalena;
using Microsoft.EntityFrameworkCore;

namespace ServerSideAnalytics.SqlServer
{
    public class SqlServerAnalyticStore : IAnalyticStore
    {
        private static readonly IMapper Mapper;
        private readonly string _connectionString;

        private string _requestTable = "SSARequest";
        private string _geoIpTable = "SSAGeoIP";

        static SqlServerAnalyticStore()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<WebRequest, SqlServerWebRequest>()
                    .ForMember(dest => dest.RemoteIpAddress, x => x.MapFrom(req => req.RemoteIpAddress.ToString()));

                cfg.CreateMap<SqlServerWebRequest, WebRequest>()
                    .ForMember(dest => dest.RemoteIpAddress, x => x.MapFrom(req => IPAddress.Parse(req.RemoteIpAddress)));
            });

            config.AssertConfigurationIsValid();

            Mapper = config.CreateMapper();
        }

        private SqlServerContext GetContext() => new SqlServerContext(_connectionString, _requestTable, _geoIpTable);
        
        public SqlServerAnalyticStore(string connectionString)
        {
            _connectionString = connectionString;
        }

        public SqlServerAnalyticStore RequestTable(string tablename)
        {
            _requestTable = tablename;
            return this;
        }

        public SqlServerAnalyticStore GeoIpTable(string tablename)
        {
            _geoIpTable = tablename;
            return this;
        }

        public async Task StoreWebRequestAsync(WebRequest request)
        {
            using (var db = GetContext())
            {
                await db.WebRequest.AddAsync(Mapper.Map<SqlServerWebRequest>(request));
                await db.SaveChangesAsync();
            }
        }

        public Task<long> CountUniqueIndentitiesAsync(DateTime day)
        {
            var from = day.Date;
            var to = day + TimeSpan.FromDays(1);
            return CountUniqueIndentitiesAsync(from, to);
        }

        public async Task<long> CountUniqueIndentitiesAsync(DateTime from, DateTime to)
        {
            using (var db = GetContext())
            {
                return await db.WebRequest.Where(x => x.Timestamp >= from && x.Timestamp <= to).GroupBy(x => x.Identity).CountAsync();
            }
        }

        public async Task<long> CountAsync(DateTime from, DateTime to)
        {
            using (var db = GetContext())
            {
                return await db.WebRequest.Where(x => x.Timestamp >= from && x.Timestamp <= to).CountAsync();
            }
        }

        public Task<IEnumerable<IPAddress>> IpAddressesAsync(DateTime day)
        {
            var from = day.Date;
            var to = day + TimeSpan.FromDays(1);
            return IpAddressesAsync(from, to);
        }

        public async Task<IEnumerable<IPAddress>> IpAddressesAsync(DateTime from, DateTime to)
        {
            using (var db = GetContext())
            {
                var ips = await db.WebRequest.Where(x => x.Timestamp >= from && x.Timestamp <= to)
                    .Select(x => x.RemoteIpAddress)
                    .Distinct()
                    .ToListAsync();

                return ips.Select(IPAddress.Parse).ToArray();
            }
        }

        public async Task<IEnumerable<WebRequest>> RequestByIdentityAsync(string identity)
        {
            using (var db = GetContext())
            {
                return await db.WebRequest.Where(x => x.Identity == identity).Select( x=> Mapper.Map<WebRequest>(x)).ToListAsync();
            }
        }

        public async Task StoreGeoIpRangeAsync(IPAddress from, IPAddress to, CountryCode countryCode)
        {
            var bytesFrom = from.GetAddressBytes();
            var bytesTo = to.GetAddressBytes();

            Array.Resize(ref bytesFrom, 16);
            Array.Resize(ref bytesTo, 16);

            using (var db = GetContext())
            {
                await db.GeoIpRange.AddAsync(new SqlServerGeoIpRange
                {
                    FromDown = BitConverter.ToInt64(bytesFrom, 0),
                    FromUp = BitConverter.ToInt64(bytesFrom, 8),

                    ToDown = BitConverter.ToInt64(bytesTo, 0),
                    ToUp = BitConverter.ToInt64(bytesTo, 8),
                    CountryCode = countryCode
                });
            }
        }

        public async Task<CountryCode> ResolveCountryCodeAsync(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            Array.Resize(ref bytes, 16);

            var down = BitConverter.ToInt64(bytes, 0);
            var up = BitConverter.ToInt64(bytes, 8);

            using (var db = GetContext())
            {
                var found = await db.GeoIpRange.FirstOrDefaultAsync(x =>
                    x.FromDown <= down && x.ToDown >= down && x.FromUp <= up && x.ToUp >= up);

                return found?.CountryCode ?? CountryCode.World;
            }
        }
    }
}