﻿using NBXplorer.Logging;
using NBXplorer.ModelBinders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.RPC;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json.Linq;
using NBXplorer.Events;
using NBXplorer.Configuration;
using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Reflection;
using System.Diagnostics;

namespace NBXplorer.Controllers
{
	[Route("v1")]
	[Authorize]
	public partial class MainController : Controller
	{
		JsonSerializerSettings _SerializerSettings;
		public MainController(
			ExplorerConfiguration config,
			ExplorerConfiguration explorerConfiguration,
			RepositoryProvider repositoryProvider,
			ChainProvider chainProvider,
			EventAggregator eventAggregator,
			BitcoinDWaitersAccessor waiters,
			AddressPoolServiceAccessor addressPoolService,
			ScanUTXOSetServiceAccessor scanUTXOSetService,
			IOptions<MvcJsonOptions> jsonOptions)
		{
			ExplorerConfiguration = explorerConfiguration;
			RepositoryProvider = repositoryProvider;
			ChainProvider = chainProvider;
			_SerializerSettings = jsonOptions.Value.SerializerSettings;
			_EventAggregator = eventAggregator;
			ScanUTXOSetService = scanUTXOSetService.Instance;
			Waiters = waiters.Instance;
			AddressPoolService = addressPoolService.Instance;
		}
		EventAggregator _EventAggregator;

		public BitcoinDWaiters Waiters
		{
			get; set;
		}
		public AddressPoolService AddressPoolService
		{
			get;
		}
		public ExplorerConfiguration ExplorerConfiguration { get; }
		public RepositoryProvider RepositoryProvider
		{
			get;
			private set;
		}
		public ChainProvider ChainProvider
		{
			get; set;
		}
		public ScanUTXOSetService ScanUTXOSetService { get; }

		[HttpGet]
		[Route("cryptos/{cryptoCode}/fees/{blockCount}")]
		public async Task<GetFeeRateResult> GetFeeRate(int blockCount, string cryptoCode)
		{
			var network = GetNetwork(cryptoCode, true);
			var waiter = Waiters.GetWaiter(network);
			EstimateSmartFeeResponse rate = null;
			try
			{
				rate = await waiter.RPC.TryEstimateSmartFeeAsyncEx(blockCount);
			}
			catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_METHOD_NOT_FOUND)
			{
			}
			if (rate == null)
			{
				var defaultFeeRate = GetDefaultFeeRate(cryptoCode);
				if (defaultFeeRate != null)
					rate = new EstimateSmartFeeResponse() { Blocks = blockCount, FeeRate = defaultFeeRate };
			}
			if (rate == null)
				throw new NBXplorerError(400, "fee-estimation-unavailable", $"It is currently impossible to estimate fees, please try again later.").AsException();
			return new GetFeeRateResult() { BlockCount = rate.Blocks, FeeRate = rate.FeeRate };
		}

		private FeeRate GetDefaultFeeRate(string cryptoCode)
		{
			return ExplorerConfiguration.ChainConfigurations.FirstOrDefault(c => c.CryptoCode.Equals(cryptoCode, StringComparison.OrdinalIgnoreCase))?.FallbackFeeRate;
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/derivations/{strategy}/addresses")]
		public KeyPathInformation GetKeyInformationFromKeyPath(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase strategy,
			[ModelBinder(BinderType = typeof(KeyPathModelBinder))]
			KeyPath keyPath)
		{
			if(strategy == null)
				throw new ArgumentNullException(nameof(strategy));
			if(keyPath == null)
				throw new ArgumentNullException(nameof(keyPath));
			var network = GetNetwork(cryptoCode, false);
			var information = strategy.Derive(keyPath);
			return new KeyPathInformation()
			{
				Address = information.ScriptPubKey.GetDestinationAddress(network.NBitcoinNetwork).ToString(),
				DerivationStrategy = strategy,
				KeyPath = keyPath,
				ScriptPubKey = information.ScriptPubKey,
				Redeem = information.Redeem,
				Feature = DerivationStrategyBase.GetFeature(keyPath)
			};
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/derivations/{strategy}/addresses/unused")]
		public async Task<KeyPathInformation> GetUnusedAddress(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase strategy, DerivationFeature feature = DerivationFeature.Deposit, int skip = 0, bool reserve = false)
		{
			if (strategy == null)
				throw new ArgumentNullException(nameof(strategy));
			var network = GetNetwork(cryptoCode, false);
			var repository = RepositoryProvider.GetRepository(network);
			if (skip >= repository.MinPoolSize)
				throw new NBXplorerError(404, "strategy-not-found", $"This strategy is not tracked, or you tried to skip too much unused addresses").AsException();
			try
			{
				var result = await repository.GetUnused(strategy, feature, skip, reserve);
				if (reserve)
				{
					while (result == null)
					{
						await AddressPoolService.GenerateAddresses(network, strategy, feature, 1);
						result = await repository.GetUnused(strategy, feature, skip, reserve);
					}
					_ = AddressPoolService.GenerateAddresses(network, strategy, feature);
				}
				return result;
			}
			catch (NotSupportedException)
			{
				throw new NBXplorerError(400, "derivation-not-supported", $"The derivation scheme {feature} is not supported").AsException();
			}
		}

		[HttpPost]
		[Route("cryptos/{cryptoCode}/derivations/{strategy}/addresses/cancelreservation")]
		public async Task<IActionResult> CancelReservation(string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase strategy, [FromBody]KeyPath[] keyPaths)
		{
			var network = GetNetwork(cryptoCode, false);
			var repo = RepositoryProvider.GetRepository(network);
			await repo.CancelReservation(strategy, keyPaths);
			return Ok();
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/scripts/{script}")]
		public async Task<IActionResult> GetKeyInformations(string cryptoCode,
			[ModelBinder(BinderType = typeof(ScriptModelBinder))] Script script)
		{
			var network = GetNetwork(cryptoCode, false);
			var repo = RepositoryProvider.GetRepository(network);
			var result = (await repo.GetKeyInformations(new[] { script }))
						   .SelectMany(k => k.Value)
						   .ToArray();
			return Json(result);
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/derivations/{strategy}/scripts/{script}")]
		public async Task<IActionResult> GetKeyInformations(string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase strategy,
			[ModelBinder(BinderType = typeof(ScriptModelBinder))] Script script)
		{
			var network = GetNetwork(cryptoCode, false);
			var repo = RepositoryProvider.GetRepository(network);
			var result = (await repo.GetKeyInformations(new[] { script }))
						   .SelectMany(k => k.Value)
						   .Where(k => k.DerivationStrategy == strategy)
						   .FirstOrDefault();
			if (result == null)
				throw new NBXplorerError(404, "script-not-found", "The script does not seem to be tracked").AsException();
			return Json(result);
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/status")]
		public async Task<IActionResult> GetStatus(string cryptoCode)
		{
			var network = GetNetwork(cryptoCode, false);
			var waiter = Waiters.GetWaiter(network);
			var chain = ChainProvider.GetChain(network);
			var repo = RepositoryProvider.GetRepository(network);
			
			var location = waiter.GetLocation();
			GetBlockchainInfoResponse blockchainInfo = null;
			GetNetworkInfoResponse networkInfo = null;
			if (waiter.RPCAvailable && waiter.NetworkInfo != null)
			{
				try
				{
					var batch = waiter.RPC.PrepareBatch();
					batch.RequestTimeout = TimeSpan.FromMinutes(1.0);
					var blockchainInfoAsync = batch.GetBlockchainInfoAsyncEx();
					var networkInfoAsync = batch.GetNetworkInfoAsync();
					await batch.SendBatchAsync();
					blockchainInfo = await blockchainInfoAsync;
					networkInfo = waiter.NetworkInfo;
				}
				catch(OperationCanceledException) // Timeout, can happen if core is really busy
				{

				}
			}

			var status = new StatusResult()
			{
				NetworkType = network.NBitcoinNetwork.NetworkType,
				CryptoCode = network.CryptoCode,
				Version = typeof(MainController).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version,
				SupportedCryptoCodes = Waiters.All().Select(w => w.Network.CryptoCode).ToArray(),
				IsFullySynched = true
			};

			if (blockchainInfo != null)
			{
				status.BitcoinStatus = new BitcoinStatus()
				{
					IsSynched = !waiter.IsSynchingCore(blockchainInfo),
					Blocks = (int)blockchainInfo.Blocks,
					Headers = (int)blockchainInfo.Headers,
					VerificationProgress = blockchainInfo.VerificationProgress,
					MinRelayTxFee = networkInfo.GetRelayFee(),
					IncrementalRelayFee = networkInfo.GetIncrementalFee(),
					Capabilities = new NodeCapabilities()
					{
						CanScanTxoutSet = waiter.RPC.Capabilities.SupportScanUTXOSet,
						CanSupportSegwit = waiter.RPC.Capabilities.SupportSegwit
					},
					ExternalAddresses = (networkInfo.localaddresses ?? Array.Empty<GetNetworkInfoResponse.LocalAddress>())
										.Select(l => $"{l.address}:{l.port}").ToArray()
				};
				status.IsFullySynched &= status.BitcoinStatus.IsSynched;
			}
			status.ChainHeight = chain.Height;
			status.SyncHeight = location == null ? (int?)null : chain.FindFork(location).Height;
			status.IsFullySynched &= blockchainInfo != null
									&& waiter.State == BitcoinDWaiterState.Ready
									&& status.SyncHeight.HasValue
									&& blockchainInfo.Headers - status.SyncHeight.Value < 3;
			if(status.IsFullySynched)
			{
				var now = DateTimeOffset.UtcNow;
				await repo.Ping();
				var pingAfter = DateTimeOffset.UtcNow;
				status.RepositoryPingTime = (pingAfter - now).TotalSeconds;
				if (status.RepositoryPingTime > 30)
				{
					Logs.Explorer.LogWarning($"Repository ping exceeded 30 seconds ({(int)status.RepositoryPingTime}), please report the issue to NBXplorer developers");
				}
			}
			return Json(status);
		}

		private NBXplorerNetwork GetNetwork(string cryptoCode, bool checkRPC)
		{
			if (cryptoCode == null)
				throw new ArgumentNullException(nameof(cryptoCode));
			cryptoCode = cryptoCode.ToUpperInvariant();
			var network = Waiters.GetWaiter(cryptoCode)?.Network;
			if (network == null)
				throw new NBXplorerException(new NBXplorerError(404, "cryptoCode-not-supported", $"{cryptoCode} is not supported"));

			if (checkRPC)
			{
				var waiter = Waiters.GetWaiter(network);
				if (waiter == null || !waiter.RPCAvailable || waiter.RPC.Capabilities == null)
					throw new NBXplorerError(400, "rpc-unavailable", $"The RPC interface is currently not available.").AsException();
			}
			return network;
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/connect")]
		public async Task<IActionResult> ConnectWebSocket(
			string cryptoCode,
			bool includeTransaction = true,
			CancellationToken cancellation = default)
		{
			if (!HttpContext.WebSockets.IsWebSocketRequest)
				return NotFound();

			GetNetwork(cryptoCode, false); // Internally check if cryptoCode is correct

			string listenAllDerivationSchemes = null;
			string listenAllTrackedSource = null;
			var listenedBlocks = new ConcurrentDictionary<string, string>();
			var listenedDerivations = new ConcurrentDictionary<(Network, DerivationStrategyBase), DerivationStrategyBase>();
			var listenedTrackedSource = new ConcurrentDictionary<(Network, TrackedSource), TrackedSource>();

			WebsocketMessageListener server = new WebsocketMessageListener(await HttpContext.WebSockets.AcceptWebSocketAsync(), _SerializerSettings);
			CompositeDisposable subscriptions = new CompositeDisposable();
			subscriptions.Add(_EventAggregator.Subscribe<Models.NewBlockEvent>(async o =>
			{
				if (listenedBlocks.ContainsKey(o.CryptoCode))
				{
					await server.Send(o);
				}
			}));
			subscriptions.Add(_EventAggregator.Subscribe<Models.NewTransactionEvent>(async o =>
			{
				var network = Waiters.GetWaiter(o.CryptoCode);
				if (network == null)
					return;

				bool forward = false;
				var derivationScheme = (o.TrackedSource as DerivationSchemeTrackedSource)?.DerivationStrategy;
				if (derivationScheme != null)
				{
					forward |= listenAllDerivationSchemes == "*" ||
								listenAllDerivationSchemes == o.CryptoCode ||
								listenedDerivations.ContainsKey((network.Network.NBitcoinNetwork, derivationScheme));
				}

				forward |= listenAllTrackedSource == "*" || listenAllTrackedSource == o.CryptoCode ||
							listenedTrackedSource.ContainsKey((network.Network.NBitcoinNetwork, o.TrackedSource));

				if (forward)
				{
					var derivation = (o.TrackedSource as DerivationSchemeTrackedSource)?.DerivationStrategy;
					await server.Send(o);
				}
			}));
			try
			{
				while (server.Socket.State == WebSocketState.Open)
				{
					object message = await server.NextMessageAsync(cancellation);
					switch (message)
					{
						case Models.NewBlockEventRequest r:
							r.CryptoCode = r.CryptoCode ?? cryptoCode;
							listenedBlocks.TryAdd(r.CryptoCode, r.CryptoCode);
							break;
						case Models.NewTransactionEventRequest r:
							var network = Waiters.GetWaiter(r.CryptoCode)?.Network;
							if (r.DerivationSchemes != null)
							{
								r.CryptoCode = r.CryptoCode ?? cryptoCode;
								if (network != null)
								{
									foreach (var derivation in r.DerivationSchemes)
									{
										var parsed = new DerivationStrategyFactory(network.NBitcoinNetwork).Parse(derivation);
										listenedDerivations.TryAdd((network.NBitcoinNetwork, parsed), parsed);
									}
								}
							}
							else if (
								// Back compat: If no derivation scheme precised and ListenAllDerivationSchemes not set, we listen all
								(r.TrackedSources == null && r.ListenAllDerivationSchemes == null) ||
								(r.ListenAllDerivationSchemes != null && r.ListenAllDerivationSchemes.Value))
							{
								listenAllDerivationSchemes = r.CryptoCode;
							}

							if (r.ListenAllTrackedSource != null && r.ListenAllTrackedSource.Value)
							{
								listenAllTrackedSource = r.CryptoCode;
							}
							else if (r.TrackedSources != null)
							{
								r.CryptoCode = r.CryptoCode ?? cryptoCode;
								if (network != null)
								{
									foreach (var trackedSource in r.TrackedSources)
									{
										if (TrackedSource.TryParse(trackedSource, out var parsed, network.NBitcoinNetwork))
											listenedTrackedSource.TryAdd((network.NBitcoinNetwork, parsed), parsed);
									}
								}
							}

							break;
						default:
							break;
					}
				}
			}
			catch when (server.Socket.State != WebSocketState.Open)
			{
			}
			finally { subscriptions.Dispose(); await server.DisposeAsync(cancellation); }
			return new EmptyResult();
		}


		[Route("cryptos/{cryptoCode}/events")]
		public async Task<JArray> GetEvents(string cryptoCode, int lastEventId = 0, int? limit = null, bool longPolling = false, CancellationToken cancellationToken = default)
		{
			// WARNING: Event though this route has 1 stream per network on upstream nbxplorer, our fork have 1 stream for all cryptos
			// So we can safely ignore cryptoCode

			if (limit != null && limit.Value < 0)
				throw new NBXplorerError(400, "invalid-limit", "limit should be more than 0").AsException();
			var network = GetNetwork(cryptoCode, false);
			TaskCompletionSource<bool> waitNextEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			Action<NewEventBase> maySetNextEvent = (NewEventBase ev) =>
			{
				waitNextEvent.TrySetResult(true);
			};
			using (CompositeDisposable subscriptions = new CompositeDisposable())
			{
				subscriptions.Add(_EventAggregator.Subscribe<NewBlockEvent>(maySetNextEvent));
				subscriptions.Add(_EventAggregator.Subscribe<NewTransactionEvent>(maySetNextEvent));
			retry:
				var repo = RepositoryProvider.GetRepository(network);
				// Actually all the
				var result = await repo.GetEvents(lastEventId, limit);
				if (result.Count == 0 && longPolling)
				{
					try
					{
						await waitNextEvent.Task.WithCancellation(cancellationToken);
						goto retry;
					}
					catch when (cancellationToken.IsCancellationRequested)
					{

					}
				}
				return new JArray(result.Select(o => o));
			}
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/transactions/{txId}")]
		public async Task<IActionResult> GetTransaction(
			[ModelBinder(BinderType = typeof(UInt256ModelBinding))]
			uint256 txId,
			bool includeTransaction = true,
			string cryptoCode = null)
		{
			var network = GetNetwork(cryptoCode, false);
			var chain = this.ChainProvider.GetChain(network);
			var result = await RepositoryProvider.GetRepository(network).GetSavedTransactions(txId);
			if (result.Length == 0)
				return NotFound();
			var tx = Utils.ToTransactionResult(includeTransaction, chain, result, network.NBitcoinNetwork);
			if (!includeTransaction)
				tx.Transaction = null;
			return Json(tx);
		}

		[HttpPost]
		[Route("cryptos/{cryptoCode}/locks/{unlockId}/cancel")]
		public async Task<IActionResult> UnlockUTXOs(string cryptoCode, string unlockId)
		{
			var network = GetNetwork(cryptoCode, false);
			var repo = RepositoryProvider.GetRepository(network);
			if (await repo.CancelMatches(unlockId))
				return Ok();
			else
				return NotFound("unlockid-not-found");
		}


		[HttpPost]
		[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}/transactions")]
		public async Task<IActionResult> LockUTXOs(string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme,
			[FromBody] LockUTXOsRequest request,
			CancellationToken cancellation = default)
		{
			if (derivationScheme == null)
				throw new ArgumentNullException(nameof(derivationScheme));
			if (request?.Destination == null)
				throw new NBXplorerException(new NBXplorerError(400, "invalid-destination", "Invalid destination address"));
			var network = GetNetwork(cryptoCode, false);
			var trackedSource = GetTrackedSource(derivationScheme, null, network.NBitcoinNetwork);
			BitcoinAddress destinationAddress = null;
			try
			{
				destinationAddress = BitcoinAddress.Create(request.Destination, network.NBitcoinNetwork);
			}
			catch
			{
				throw new NBXplorerException(new NBXplorerError(400, "invalid-destination", "Invalid destination address"));
			}
			if (request.Amount == null || request.Amount <= Money.Zero)
				throw new NBXplorerException(new NBXplorerError(400, "invalid-amount", "amount should be equal or less than 0 satoshi"));

			var repo = RepositoryProvider.GetRepository(network);

			Repository.DBLock walletLock = null;
			try
			{
				walletLock = await repo.TakeWalletLock(derivationScheme, cancellation);

				var psbtRequest = new CreatePSBTRequest();
				psbtRequest.Destinations.Add(new CreatePSBTDestination()
				{
					Amount = request.Amount,
					Destination = BitcoinAddress.Create(request.Destination, network.NBitcoinNetwork),
					SubstractFees = request.SubstractFees
				});
				psbtRequest.FeePreference = new FeePreference() { ExplicitFeeRate = request.FeeRate, BlockTarget = 6 };
				psbtRequest.ReserveChangeAddress = true;

				var psbtActionResult = await this.CreatePSBT(network, derivationScheme, network.Serializer.ToJObject(psbtRequest));
				var psbt = ((psbtActionResult as JsonResult)?.Value as CreatePSBTResponse)?.PSBT;
				var changeAddress = ((psbtActionResult as JsonResult)?.Value as CreatePSBTResponse)?.ChangeAddress;
				if (psbt == null)
					return psbtActionResult;
				
				LockUTXOsResponse result = new LockUTXOsResponse();
				psbt.TryGetFee(out var fee);
				result.Fee = fee;

				result.SpentCoins = psbt.Inputs.Select(i => new LockUTXOsResponse.SpentCoin()
				{
					KeyPath = i.HDKeyPaths.First().Value.Item2,
					Outpoint = i.PrevOut,
					Value = i.GetTxOut().Value
				})
				.ToArray();

				var tx = psbt.GetGlobalTransaction();
				foreach (var input in tx.Inputs)
				{
					var psbtInput = psbt.Inputs.FindIndexedInput(input.PrevOut);
					var coin = psbtInput.GetSignableCoin() ?? psbtInput.GetCoin();
					if (coin is ScriptCoin scriptCoin)
					{
						if (scriptCoin.RedeemType == RedeemType.P2SH)
						{
							input.ScriptSig = new Script(Op.GetPushOp(scriptCoin.Redeem.ToBytes()));
						}
						else if (scriptCoin.RedeemType == RedeemType.WitnessV0)
						{
							input.WitScript = new Script(Op.GetPushOp(scriptCoin.Redeem.ToBytes()));
							if (scriptCoin.IsP2SH)
								input.ScriptSig = new Script(Op.GetPushOp(scriptCoin.Redeem.WitHash.ScriptPubKey.ToBytes()));
						}
					}
				}
				result.Transaction = tx.Clone();

				if (changeAddress != null)
				{
					var changeOutput = psbt.Outputs.Where(c => c.ScriptPubKey == changeAddress.ScriptPubKey).FirstOrDefault();
					if (changeOutput != null)
					{
						result.ChangeInformation = new LockUTXOsResponse.ChangeInfo()
						{
							KeyPath = changeOutput.HDKeyPaths.First().Value.Item2,
							Value = changeOutput.Value
						};
					}
				}
				tx.MarkLockUTXO();

				TrackedTransactionKey trackedTransactionKey = new TrackedTransactionKey(tx.GetHash(), null, false);
				TrackedTransaction trackedTransaction = new TrackedTransaction(trackedTransactionKey, trackedSource, tx, new Dictionary<Script, KeyPath>());
				foreach(var c in psbt.Inputs.OfType<PSBTCoin>().Concat(psbt.Outputs).Where(c => c.HDKeyPaths.Any()))
				{
					trackedTransaction.KnownKeyPathMapping.TryAdd(c.GetCoin().ScriptPubKey, c.HDKeyPaths.First().Value.Item2);
				}
				var cancellableMatch = await repo.SaveMatches(new[] { trackedTransaction }, true);
				result.UnlockId = cancellableMatch.Key;
				return Json(result);
			}
			catch (NotEnoughFundsException)
			{
				throw new NBXplorerException(new NBXplorerError(400, "not-enough-funds", "Not enough funds for doing this transaction"));
			}
			finally
			{
				if (walletLock != null)
					await walletLock.ReleaseLock();
			}
		}

		[HttpPost]
		[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}")]
		[Route("cryptos/{cryptoCode}/addresses/{address}")]
		public async Task<IActionResult> TrackWallet(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme,
			[ModelBinder(BinderType = typeof(BitcoinAddressModelBinder))]
			BitcoinAddress address)
		{
			var network = GetNetwork(cryptoCode, false);
			TrackedSource trackedSource = GetTrackedSource(derivationScheme, address, network.NBitcoinNetwork);
			if (trackedSource == null)
				return NotFound();
			if (trackedSource is DerivationSchemeTrackedSource dts)
			{
				foreach (var feature in Enum.GetValues(typeof(DerivationFeature)).Cast<DerivationFeature>())
				{
					await RepositoryProvider.GetRepository(network).GenerateAddresses(dts.DerivationStrategy, feature, new GenerateAddressQuery(minAddresses: 3, null));
				}
				foreach (var feature in Enum.GetValues(typeof(DerivationFeature)).Cast<DerivationFeature>())
				{
					_ = AddressPoolService.GenerateAddresses(network, dts.DerivationStrategy, feature);
				}
			}
			else if (trackedSource is IDestination ats)
			{
				await RepositoryProvider.GetRepository(network).Track(ats);
			}
			return Ok();
		}

		private static TrackedSource GetTrackedSource(DerivationStrategyBase derivationScheme, BitcoinAddress address, Network network)
		{
			TrackedSource trackedSource = null;
			if (address != null)
				trackedSource = new AddressTrackedSource(address);
			if (derivationScheme != null)
				trackedSource = new DerivationSchemeTrackedSource(derivationScheme, network);
			return trackedSource;
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}/transactions/{txId?}")]
		[Route("cryptos/{cryptoCode}/addresses/{address}/transactions/{txId?}")]
		public async Task<IActionResult> GetTransactions(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme,
			[ModelBinder(BinderType = typeof(BitcoinAddressModelBinder))]
			BitcoinAddress address,
			[ModelBinder(BinderType = typeof(UInt256ModelBinding))]
			uint256 txId = null,
			bool includeTransaction = true)
		{
			var network = GetNetwork(cryptoCode, false);
			var trackedSource = GetTrackedSource(derivationScheme, address, network.NBitcoinNetwork);
			if (trackedSource == null)
				throw new ArgumentNullException(nameof(trackedSource));
			TransactionInformation fetchedTransactionInfo = null;

			var chain = ChainProvider.GetChain(network);
			var repo = RepositoryProvider.GetRepository(network);

			var response = new GetTransactionsResponse();
			int currentHeight = chain.Height;
			response.Height = currentHeight;
			var txs = await GetAnnotatedTransactions(repo, chain, trackedSource, true, txId);
			foreach (var item in new[]
			{
					new
					{
						TxSet = response.ConfirmedTransactions,
						AnnotatedTx = txs.ConfirmedTransactions
					},
					new
					{
						TxSet = response.UnconfirmedTransactions,
						AnnotatedTx = txs.UnconfirmedTransactions
					},
					new
					{
						TxSet = response.ReplacedTransactions,
						AnnotatedTx = txs.ReplacedTransactions
					},
				})
			{
				foreach (var tx in item.AnnotatedTx)
				{
					var txInfo = new TransactionInformation()
					{
						BlockHash = tx.Height.HasValue ? tx.Record.BlockHash : null,
						Height = tx.Height,
						TransactionId = tx.Record.TransactionHash,
						Transaction = includeTransaction ? tx.Record.Transaction : null,
						Confirmations = tx.Height.HasValue ? currentHeight - tx.Height.Value + 1 : 0,
						Timestamp = txs.GetByTxId(tx.Record.TransactionHash).Select(t => t.Record.FirstSeen).First(),
						Inputs = tx.Record.SpentOutpoints.Select(o => txs.GetUTXO(o)).Where(o => o != null).ToList(),
						Outputs = tx.Record.GetReceivedOutputs().ToList()
					};

					if (txId == null || txId == txInfo.TransactionId)
						item.TxSet.Transactions.Add(txInfo);
					if (txId != null && txId == txInfo.TransactionId)
						fetchedTransactionInfo = txInfo;

					txInfo.BalanceChange = txInfo.Outputs.Select(o => o.Value).Sum() - txInfo.Inputs.Select(o => o.Value).Sum();
				}
			}



			if (txId == null)
			{
				return Json(response);
			}
			else if (fetchedTransactionInfo == null)
			{
				return NotFound();
			}
			else
			{
				return Json(fetchedTransactionInfo);
			}
		}

		[HttpPost]
		[Route("cryptos/{cryptoCode}/rescan")]
		public async Task<IActionResult> Rescan(string cryptoCode, [FromBody]RescanRequest rescanRequest)
		{
			if (rescanRequest == null)
				throw new ArgumentNullException(nameof(rescanRequest));
			if (rescanRequest?.Transactions == null)
				throw new NBXplorerException(new NBXplorerError(400, "transactions-missing", "You must specify 'transactions'"));

			bool willFetchTransactions = rescanRequest.Transactions.Any(t => t.Transaction == null);
			bool needTxIndex = rescanRequest.Transactions.Any(t => t.Transaction == null && t.BlockId == null);
			var network = GetNetwork(cryptoCode, willFetchTransactions);

			var rpc = Waiters.GetWaiter(cryptoCode).RPC.PrepareBatch();
			var repo = RepositoryProvider.GetRepository(network);

			var fetchingTransactions = rescanRequest
				.Transactions
				.Select(t => FetchTransaction(rpc, t))
				.ToArray();

			await rpc.SendBatchAsync();
			await Task.WhenAll(fetchingTransactions);

			var transactions = fetchingTransactions.Select(t => t.GetAwaiter().GetResult())
												   .Where(tx => tx.Transaction != null)
												   .ToArray();

			foreach (var txs in transactions.GroupBy(t => t.BlockId, t => (t.Transaction, t.BlockTime))
											.OrderBy(t => t.First().BlockTime))
			{
				await repo.SaveTransactions(txs.First().BlockTime, txs.Select(t => t.Transaction).ToArray(), txs.Key);
				foreach (var tx in txs)
				{
					var matches = await repo.GetMatches(tx.Transaction, txs.Key, tx.BlockTime, false);
					await repo.SaveMatches(matches);
					_ = AddressPoolService.GenerateAddresses(network, matches);
				}
			}
			return Ok();
		}

		async Task<(uint256 BlockId, Transaction Transaction, DateTimeOffset BlockTime)> FetchTransaction(RPCClient rpc, RescanRequest.TransactionToRescan transaction)
		{
			if (transaction.Transaction != null)
			{
				if (transaction.BlockId == null)
					throw new NBXplorerException(new NBXplorerError(400, "block-id-missing", "You must specify 'transactions[].blockId' if you specified 'transactions[].transaction'"));
				var blockTime = await rpc.GetBlockTimeAsync(transaction.BlockId, false);
				if (blockTime == null)
					return (null, null, default);
				return (transaction.BlockId, transaction.Transaction, blockTime.Value);
			}
			else if (transaction.TransactionId != null)
			{
				if (transaction.BlockId != null)
				{
					var getTx = rpc.GetRawTransactionAsync(transaction.TransactionId, transaction.BlockId, false);
					var blockTime = await rpc.GetBlockTimeAsync(transaction.BlockId, false);
					if (blockTime == null)
						return (null, null, default);
					return (transaction.BlockId, await getTx, blockTime.Value);
				}
				else
				{
					try
					{
						var txInfo = await rpc.GetRawTransactionInfoAsync(transaction.TransactionId);
						return (txInfo.BlockHash, txInfo.Transaction, txInfo.BlockTime.Value);
					}
					catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
					{
						return (null, null, default);
					}
				}
			}
			else
			{
				throw new NBXplorerException(new NBXplorerError(400, "transaction-id-missing", "You must specify 'transactions[].transactionId' or 'transactions[].transaction'"));
			}
		}

		[HttpPost]
		[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}/utxos/scan")]
		public IActionResult ScanUTXOSet(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme, int? batchSize = null, int? gapLimit = null, int? from = null)
		{
			var network = this.GetNetwork(cryptoCode, true);
			var waiter = this.Waiters.GetWaiter(network);
			if (!waiter.RPC.Capabilities.SupportScanUTXOSet)
				throw new NBXplorerError(405, "scanutxoset-not-suported", "ScanUTXOSet is not supported for this currency").AsException();

			ScanUTXOSetOptions options = new ScanUTXOSetOptions();
			if (batchSize != null)
				options.BatchSize = batchSize.Value;
			if (gapLimit != null)
				options.GapLimit = gapLimit.Value;
			if (from != null)
				options.From = from.Value;
			if (!ScanUTXOSetService.EnqueueScan(network, derivationScheme, options))
				throw new NBXplorerError(409, "scanutxoset-in-progress", "ScanUTXOSet has already been called for this derivationScheme").AsException();
			return Ok();
		}

		//[HttpPost]
		//[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}/")]
		//public async Task<>


		[HttpGet]
		[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}/balances")]
		public async Task<GetBalanceResponse> GetBalance(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(ModelBinders.DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme)
		{
			var network = GetNetwork(cryptoCode, false);
			var chain = ChainProvider.GetChain(network);
			var repo = RepositoryProvider.GetRepository(network);
			var trackedSource = GetTrackedSource(derivationScheme, null, network.NBitcoinNetwork);

			GetBalanceResponse response = new GetBalanceResponse();

			var transactions = await GetAnnotatedTransactions(repo, chain, trackedSource, false);

			response.Spendable = CalculateBalance(transactions, true);
			response.Total = CalculateBalance(transactions, false);

			return response;
		}

		private Money CalculateBalance(AnnotatedTransactionCollection transactions, bool excludeLocksUTXOS)
		{
			var changes = new UTXOChanges();

			var states = UTXOStateResult.CreateStates(excludeLocksUTXOS,
														transactions.UnconfirmedTransactions.Select(c => c.Record),
														transactions.ConfirmedTransactions.Select(c => c.Record));

			changes.Confirmed = SetUTXOChange(states.Confirmed);
			changes.Unconfirmed = SetUTXOChange(states.Unconfirmed, states.Confirmed.Actual);

			return changes.GetUnspentCoins().Select(c => c.Amount).Sum();
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}/utxos/scan")]
		public IActionResult GetScanUTXOSetInfromation(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme)
		{
			var network = this.GetNetwork(cryptoCode, false);
			var info = ScanUTXOSetService.GetInformation(network, derivationScheme);
			if (info == null)
				throw new NBXplorerError(404, "scanutxoset-info-not-found", "ScanUTXOSet has not been called with this derivationScheme of the result has expired").AsException();
			return Json(info);
		}

		[HttpGet]
		[Route("cryptos/{cryptoCode}/derivations/{derivationScheme}/utxos")]
		[Route("cryptos/{cryptoCode}/addresses/{address}/utxos")]
		public async Task<UTXOChanges> GetUTXOs(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme,
			[ModelBinder(BinderType = typeof(BitcoinAddressModelBinder))]
			BitcoinAddress address)
		{
			var network = GetNetwork(cryptoCode, false);
			var trackedSource = GetTrackedSource(derivationScheme, address, network.NBitcoinNetwork);
			UTXOChanges changes = null;
			if (trackedSource == null)
				throw new ArgumentNullException(nameof(trackedSource));

			var chain = ChainProvider.GetChain(network);
			var repo = RepositoryProvider.GetRepository(network);

			changes = new UTXOChanges();
			changes.CurrentHeight = chain.Height;
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
			var transactions = await GetAnnotatedTransactions(repo, chain, trackedSource, false);

			var states = UTXOStateResult.CreateStates(true, transactions.UnconfirmedTransactions.Select(c => c.Record),
													transactions.ConfirmedTransactions.Select(c => c.Record));

			changes.Confirmed = SetUTXOChange(states.Confirmed);
			changes.Unconfirmed = SetUTXOChange(states.Unconfirmed, states.Confirmed.Actual);



			FillUTXOsInformation(changes.Confirmed.UTXOs, transactions, changes.CurrentHeight);
			FillUTXOsInformation(changes.Unconfirmed.UTXOs, transactions, changes.CurrentHeight);

			stopwatch.Stop();
			if (ExplorerConfiguration.AutoPruningTime != null &&
			   stopwatch.Elapsed > ExplorerConfiguration.AutoPruningTime.Value)
			{
				await AttemptPrune(repo, transactions, states);
			}

			changes.TrackedSource = trackedSource;
			changes.DerivationStrategy = (trackedSource as DerivationSchemeTrackedSource)?.DerivationStrategy;

			return changes;
		}

		private async Task AttemptPrune(Repository repo, AnnotatedTransactionCollection transactions, UTXOStateResult states)
		{
			var network = repo.Network;
			var trackedSource = transactions.TrackedSource;
			var quarter = states.Confirmed.Actual.GetQuarterTransactionTime();
			if (quarter != null)
			{
				Logs.Explorer.LogInformation($"{network.CryptoCode}: Pruning needed for {trackedSource.ToPrettyString()}...");

				// Step 1. Mark all transactions whose UTXOs have been all spent for long enough (quarter of first seen time of all transaction)
				var prunableIds = states.Confirmed.Actual
								.UTXOByOutpoint
								.Prunable
								.Where(p => OldEnough(transactions, p.PrunedBy, quarter.Value))
								.Select(p => transactions.GetByTxId(p.TransactionId).First())
								.Select(p => p.Record.TransactionHash)
								.ToHashSet();

				// Step2. Make sure that all their parent are also prunable (Ancestors first)
				if (prunableIds.Count != 0)
				{
					foreach (var tx in transactions.ConfirmedTransactions)
					{
						if (prunableIds.Count == 0)
							break;
						if (!prunableIds.Contains(tx.Record.TransactionHash))
							continue;
						foreach (var parent in tx.Record.SpentOutpoints
														.Select(spent => transactions.GetByTxId(spent.Hash)?.FirstOrDefault())
														.Where(parent => parent != null)
														.Where(parent => !prunableIds.Contains(parent.Record.TransactionHash)))
						{
							prunableIds.Remove(tx.Record.TransactionHash);
						}
					}
				}

				if (prunableIds.Count == 0)
					Logs.Explorer.LogInformation($"{network.CryptoCode}: Impossible to prune {trackedSource.ToPrettyString()}, if you wish to improve performance, please decrease the number of UTXOs");
				else
				{
					await repo.Prune(trackedSource, prunableIds
													.SelectMany(id => transactions.GetByTxId(id).Select(c => c.Record))
													.ToList());
					Logs.Explorer.LogInformation($"{network.CryptoCode}: Pruned {prunableIds.Count} transactions");
				}
			}
		}

		private bool OldEnough(AnnotatedTransactionCollection transactions, uint256 prunedBy, DateTimeOffset pruneBefore)
		{
			// Let's make sure that the transaction that made this transaction pruned has enough confirmations
			var tx = transactions.GetByTxId(prunedBy);
			if (tx == null)
				return false;
			var firstSeen = tx.Where(t => t.Height != null)
								  .Select(t => t.Record.FirstSeen)
								  .FirstOrDefault();
			return firstSeen <= pruneBefore;
		}

		private static bool IsMatching(TrackedSource trackedSource, Script s, AnnotatedTransactionCollection transactions)
		{
			if (trackedSource is DerivationSchemeTrackedSource dsts)
				return transactions.GetKeyPath(s) != null;
			else if (trackedSource is IDestination addr)
				return addr.ScriptPubKey == s;
			else
				throw new NotSupportedException();
		}

		static int[] MaxValue = new[] { int.MaxValue };
		private void FillUTXOsInformation(List<UTXO> utxos, AnnotatedTransactionCollection transactions, int currentHeight)
		{
			for (int i = 0; i < utxos.Count; i++)
			{
				var utxo = utxos[i];
				utxo.KeyPath = transactions.GetKeyPath(utxo.ScriptPubKey);
				if (utxo.KeyPath != null)
					utxo.Feature = DerivationStrategyBase.GetFeature(utxo.KeyPath);
				var txHeight = transactions.GetByTxId(utxo.Outpoint.Hash)
									.Select(t => t.Height)
									.Where(h => h.HasValue)
									.Select(t => t.Value)
									.Concat(MaxValue)
									.Min();
				var firstSeen = transactions
					.GetByTxId(utxo.Outpoint.Hash)
					.Select(o => o.Record.FirstSeen)
					.FirstOrDefault();
				var isUnconf = txHeight == MaxValue[0];
				utxo.Confirmations = isUnconf ? 0 : currentHeight - txHeight + 1;
				utxo.Timestamp = firstSeen;
			}
		}

		private UTXOChange SetUTXOChange(UTXOStates states, UTXOState substract = null)
		{
			substract = substract ?? new UTXOState();
			var substractedSpent = new HashSet<OutPoint>(substract.SpentUTXOs);
			var substractedReceived = new HashSet<OutPoint>(substract.UTXOByOutpoint.Select(u => u.Key));

			UTXOChange change = new UTXOChange();
			states.Known = states.Known ?? new UTXOState();

			foreach (var coin in states.Actual.UTXOByOutpoint)
			{
				if (!states.Known.UTXOByOutpoint.ContainsKey(coin.Key) &&
					!substractedReceived.Contains(coin.Key))
					change.UTXOs.Add(new UTXO(coin.Value));
			}

			foreach (var outpoint in states.Actual.SpentUTXOs)
			{
				if (!states.Known.SpentUTXOs.Contains(outpoint) &&
					(states.Known.UTXOByOutpoint.ContainsKey(outpoint) || substractedReceived.Contains(outpoint)) &&
					!substractedSpent.Contains(outpoint))
					change.SpentOutpoints.Add(outpoint);
			}
			return change;
		}

		private async Task<AnnotatedTransactionCollection> GetAnnotatedTransactions(Repository repo, SlimChain chain, TrackedSource trackedSource, bool excludeLocks, uint256 txId = null)
		{
			var transactions = (await repo.GetTransactions(trackedSource, txId)).Where(t => !excludeLocks || !t.IsLockUTXO());

			// If the called is interested by only a single txId, we need to fetch the parents as well
			if (txId != null)
			{
				var spentOutpoints = transactions.SelectMany(t => t.SpentOutpoints.Select(o => o.Hash)).ToHashSet();
				var gettingParents = spentOutpoints.Select(async h => await repo.GetTransactions(trackedSource, h)).ToList();
				await Task.WhenAll(gettingParents);
				transactions = gettingParents.SelectMany(p => p.GetAwaiter().GetResult()).Concat(transactions).ToArray();
			}

			var annotatedTransactions = new AnnotatedTransactionCollection(
				transactions.Select(t => new AnnotatedTransaction(t, chain))
				.ToList(), trackedSource);


			var cleaned = annotatedTransactions.DuplicatedTransactions.Where(c => (DateTimeOffset.UtcNow - c.Record.Inserted) > TimeSpan.FromDays(1.0)).Select(c => c.Record).ToArray();
			if (cleaned.Length != 0)
			{
				foreach (var tx in cleaned)
				{
					_EventAggregator.Publish(new EvictedTransactionEvent(tx.TransactionHash));
				}
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
				// Can be eventually consistent
				repo.CleanTransactions(annotatedTransactions.TrackedSource, cleaned.ToList());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
			}
			return annotatedTransactions;
		}

		[HttpPost]
		[Route("cryptos/{cryptoCode}/transactions")]
		public async Task<BroadcastResult> Broadcast(
			string cryptoCode,
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase extPubKey, // For back compat
			[ModelBinder(BinderType = typeof(DerivationStrategyModelBinder))]
			DerivationStrategyBase derivationScheme,
			[ModelBinder(BinderType = typeof(BitcoinAddressModelBinder))]
			BitcoinAddress address)
		{
			var network = GetNetwork(cryptoCode, true);
			var trackedSource = GetTrackedSource(derivationScheme ?? extPubKey, address, network.NBitcoinNetwork);
			var tx = network.NBitcoinNetwork.Consensus.ConsensusFactory.CreateTransaction();
			var stream = new BitcoinStream(Request.Body, false);
			tx.ReadWrite(stream);

			var waiter = this.Waiters.GetWaiter(network);
			var repo = RepositoryProvider.GetRepository(network);
			var chain = ChainProvider.GetChain(network);
			RPCException rpcEx = null;
			try
			{
				await waiter.RPC.SendRawTransactionAsync(tx);
				return new BroadcastResult(true);
			}
			catch (RPCException ex)
			{
				rpcEx = ex;
				Logs.Explorer.LogInformation($"{network.CryptoCode}: Transaction {tx.GetHash()} failed to broadcast (Code: {ex.RPCCode}, Message: {ex.RPCCodeMessage}, Details: {ex.Message} )");
				if (trackedSource != null && ex.Message.StartsWith("Missing inputs", StringComparison.OrdinalIgnoreCase))
				{
					Logs.Explorer.LogInformation($"{network.CryptoCode}: Trying to broadcast unconfirmed of the wallet");
					var transactions = await GetAnnotatedTransactions(repo, chain, trackedSource, true);
					foreach (var existing in transactions.UnconfirmedTransactions)
					{
						var t = existing.Record.Transaction ?? (await repo.GetSavedTransactions(existing.Record.TransactionHash)).Select(c => c.Transaction).FirstOrDefault();
						if (t == null)
							continue;
						try
						{
							await waiter.RPC.SendRawTransactionAsync(t);
						}
						catch { }
					}

					try
					{
						await waiter.RPC.SendRawTransactionAsync(tx);
						Logs.Explorer.LogInformation($"{network.CryptoCode}: Broadcast success");
						return new BroadcastResult(true);
					}
					catch (RPCException)
					{
						Logs.Explorer.LogInformation($"{network.CryptoCode}: Transaction {tx.GetHash()} failed to broadcast (Code: {ex.RPCCode}, Message: {ex.RPCCodeMessage}, Details: {ex.Message} )");
					}
				}
				return new BroadcastResult(false)
				{
					RPCCode = rpcEx.RPCCode,
					RPCCodeMessage = rpcEx.RPCCodeMessage,
					RPCMessage = rpcEx.Message
				};
			}
		}
	}
}
