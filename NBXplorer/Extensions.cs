﻿using Microsoft.AspNetCore.Hosting;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NBitcoin;
using NBXplorer.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc;
using NBXplorer.Filters;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using NBXplorer.DerivationStrategy;
using NBitcoin.Crypto;
using NBXplorer.Models;
using System.IO;
using NBXplorer.Logging;
using System.Net;
using NBitcoin.RPC;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.Authentication;
using NBXplorer.Authentication;
using NBitcoin.DataEncoders;
using System.Text.RegularExpressions;
using NBXplorer.MessageBrokers;

namespace NBXplorer
{
	public static class Extensions
	{
		internal static Task WaitOneAsync(this WaitHandle waitHandle)
		{
			if(waitHandle == null)
				throw new ArgumentNullException("waitHandle");

			var tcs = new TaskCompletionSource<bool>();
			var rwh = ThreadPool.RegisterWaitForSingleObject(waitHandle,
				delegate
				{
					tcs.TrySetResult(true);
				}, null, TimeSpan.FromMinutes(1.0), true);
			var t = tcs.Task;
			t.ContinueWith(_ => rwh.Unregister(null));
			return t;
		}
		internal static uint160 GetHash(this DerivationStrategyBase derivation)
		{
			var data = Encoding.UTF8.GetBytes(derivation.ToString());
			return new uint160(Hashes.RIPEMD160(data, data.Length));
		}
		internal static uint160 GetHash(this TrackedSource trackedSource)
		{
			if (trackedSource is DerivationSchemeTrackedSource t)
				return t.DerivationStrategy.GetHash();
			var data = Encoding.UTF8.GetBytes(trackedSource.ToString());
			return new uint160(Hashes.RIPEMD160(data, data.Length));
		}


		public static async Task<DateTimeOffset?> GetBlockTimeAsync(this RPCClient client, uint256 blockId, bool throwIfNotFound = true)
		{
			var response = await client.SendCommandAsync(new RPCRequest("getblockheader", new object[] { blockId }), throwIfNotFound).ConfigureAwait(false);
			if(throwIfNotFound)
				response.ThrowIfError();
			if(response.Error != null && response.Error.Code == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
				return null;
			if(response.Result["time"] != null)
			{
				return NBitcoin.Utils.UnixTimeToDateTime((uint)response.Result["time"]);
			}
			return null;
		}

		public static NewTransactionEvent SetMatch(this NewTransactionEvent evt, MatchedTransaction match)
		{
			evt.TrackedSource = match.TrackedSource;
			var derivation = (match.TrackedSource as DerivationSchemeTrackedSource)?.DerivationStrategy;
			evt.Outputs.AddRange(match.TrackedTransaction.GetReceivedOutputs(evt.TrackedSource));
			evt.DerivationStrategy = derivation;
			return evt;
		}

		public static async Task<MatchedTransaction[]> GetMatches(this Repository repository, Transaction tx, uint256 blockId, DateTimeOffset now)
		{
			var matches = new Dictionary<string, MatchedTransaction>();
			HashSet<Script> inputScripts = new HashSet<Script>();
			HashSet<Script> outputScripts = new HashSet<Script>();
			HashSet<Script> scripts = new HashSet<Script>();
			foreach(var input in tx.Inputs)
			{
				var signer = input.GetSigner();
				if(signer != null)
				{
					inputScripts.Add(signer.ScriptPubKey);
					scripts.Add(signer.ScriptPubKey);
				}
			}

			foreach(var output in tx.Outputs)
			{
				outputScripts.Add(output.ScriptPubKey);
				scripts.Add(output.ScriptPubKey);
			}

			var keyInformations = await repository.GetKeyInformations(scripts.ToArray());
			foreach(var keyInfoByScripts in keyInformations)
			{
				foreach(var keyInfo in keyInfoByScripts.Value)
				{
					var matchesGroupingKey = keyInfo.DerivationStrategy?.ToString() ?? keyInfo.ScriptPubKey.ToHex();
					if (!matches.TryGetValue(matchesGroupingKey, out MatchedTransaction match))
					{
						match = new MatchedTransaction();
						matches.Add(matchesGroupingKey, match);
						match.TrackedSource = keyInfo.TrackedSource;
						match.TrackedTransaction = new TrackedTransaction(new TrackedTransactionKey(tx.GetHash(), blockId, false), tx, new Dictionary<Script, KeyPath>())
						{
							FirstSeen = now,
							Inserted = now
						};
					}
					match.TrackedTransaction.KnownKeyPathMapping.TryAdd(keyInfo.ScriptPubKey, keyInfo.KeyPath);
				}
			}
			foreach(var m in matches.Values)
			{
				m.TrackedTransaction.KnownKeyPathMappingUpdated();
			}
			return matches.Values.ToArray();
		}

		class MVCConfigureOptions : IConfigureOptions<MvcJsonOptions>
		{
			public void Configure(MvcJsonOptions options)
			{
				new Serializer(null).ConfigureSerializer(options.SerializerSettings);
			}
		}

		public class ConfigureCookieFileBasedConfiguration : IConfigureNamedOptions<BasicAuthenticationOptions>
		{
			CookieRepository _CookieRepo;
			public ConfigureCookieFileBasedConfiguration(CookieRepository cookieRepo)
			{
				_CookieRepo = cookieRepo;
			}

			public void Configure(string name, BasicAuthenticationOptions options)
			{
				if(name == "Basic")
				{
					var creds = _CookieRepo.GetCredentials();
					if(creds != null)
					{
						options.Username = creds.UserName;
						options.Password = creds.Password;
					}
				}
			}

			public void Configure(BasicAuthenticationOptions options)
			{
				Configure(null, options);
			}
		}

		public static AuthenticationBuilder AddNBXplorerAuthentication(this AuthenticationBuilder builder)
		{
			builder.Services.AddSingleton<IConfigureOptions<BasicAuthenticationOptions>, ConfigureCookieFileBasedConfiguration>();
			return builder.AddScheme<Authentication.BasicAuthenticationOptions, Authentication.BasicAuthenticationHandler>("Basic", o =>
			{

			});
		}

		public static IServiceCollection AddNBXplorer(this IServiceCollection services)
		{
			services.AddSingleton<IObjectModelValidator, NoObjectModelValidator>();
			services.Configure<MvcOptions>(mvc =>
			{
				mvc.Filters.Add(new NBXplorerExceptionFilter());
			});

			services.AddSingleton<IConfigureOptions<MvcJsonOptions>, MVCConfigureOptions>();
			services.TryAddSingleton<ChainProvider>();

			services.TryAddSingleton<CookieRepository>();
			services.TryAddSingleton<RepositoryProvider>();
			services.TryAddSingleton<EventAggregator>();
			services.TryAddSingleton<AddressPoolServiceAccessor>();
			services.AddSingleton<IHostedService, AddressPoolService>();
			services.TryAddSingleton<BitcoinDWaitersAccessor>();
			services.AddSingleton<IHostedService, ScanUTXOSetService>();
			services.TryAddSingleton<ScanUTXOSetServiceAccessor>();
			services.AddSingleton<IHostedService, BitcoinDWaiters>();
			services.AddSingleton<IHostedService, BrokerHostedService>();

			services.AddSingleton<ExplorerConfiguration>(o => o.GetRequiredService<IOptions<ExplorerConfiguration>>().Value);

			services.AddSingleton<NBXplorerNetworkProvider>(o =>
			{
				var c = o.GetRequiredService<ExplorerConfiguration>();
				return c.NetworkProvider;
			});
			services.TryAddSingleton<RPCClientProvider>();
			return services;
		}

		public static IServiceCollection ConfigureNBxplorer(this IServiceCollection services, IConfiguration conf)
		{
			services.Configure<ExplorerConfiguration>(o =>
			{
				o.LoadArgs(conf);
			});
			return services;
		}

		internal static string ToPrettyString(this TrackedSource trackedSource)
		{
			if (trackedSource is DerivationSchemeTrackedSource derivation)
			{
				var strategy = derivation.DerivationStrategy.ToString();
				if (strategy.Length > 35)
				{
					strategy = strategy.Substring(0, 10) + "..." + strategy.Substring(strategy.Length - 20);
				}
				return strategy;
			}
			else if (trackedSource is AddressTrackedSource addressDerivation)
			{
				return addressDerivation.Address.ToString();
			}
			else
			{
				return trackedSource.ToString();
			}
		}

		internal class NoObjectModelValidator : IObjectModelValidator
		{
			public void Validate(ActionContext actionContext, ValidationStateDictionary validationState, string prefix, object model)
			{

			}
		}
	}
}
