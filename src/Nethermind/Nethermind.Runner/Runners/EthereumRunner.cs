﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.TransactionPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Module;
using Nethermind.KeyStore;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Crypto;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Runner.Config;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;
using PingMessageSerializer = Nethermind.Network.P2P.PingMessageSerializer;
using PongMessageSerializer = Nethermind.Network.P2P.PongMessageSerializer;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IRunner
    {
        private static readonly bool HiveEnabled =
            Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";
        private static ILogManager _logManager;
        private static ILogger _logger;

        private IRpcModuleProvider _rpcModuleProvider;
        private IConfigProvider _configProvider;
        private IInitConfig _initConfig;
        private INetworkHelper _networkHelper;
        
        private const string UnsecuredNodeKeyFilePath = "node.key.plain";
        private PrivateKey _nodeKey;
        private ChainSpec _chainSpec;
        private ICryptoRandom _cryptoRandom = new CryptoRandom();
        private ISigner _signer = new Signer();
        private IJsonSerializer _jsonSerializer = new UnforgivingJsonSerializer();
        private CancellationTokenSource _runnerCancellation;

        private IBlockchainProcessor _blockchainProcessor;
        private IDiscoveryApp _discoveryApp;
        private IDiscoveryManager _discoveryManager;
        private IMessageSerializationService _messageSerializationService = new MessageSerializationService();
        private INodeFactory _nodeFactory;
        private INodeStatsProvider _nodeStatsProvider;
        private IPerfService _perfService;
        private ITransactionPool _transactionPool;
        private ITransactionPoolInfoProvider _transactionPoolInfoProvider;
        private IReceiptStorage _receiptStorage;
        private IEthereumSigner _ethereumSigner;
        private ISynchronizationManager _syncManager;
        private IKeyStore _keyStore;
        private IPeerManager _peerManager;
        private IBlockTree _blockTree;
        private IBlockValidator _blockValidator;
        private IBlockDataRecoveryStep _recoveryStep;
        private IBlockProcessor _blockProcessor;
        private IRewardCalculator _rewardCalculator;
        private ISpecProvider _specProvider;
        private ISealEngine _sealEngine;
        private IBlockProducer _blockProducer;
        private IRlpxPeer _rlpxPeer;
        private IDbProvider _dbProvider;
        private readonly ITimestamp _timestamp = new Timestamp();
        private IStateProvider _stateProvider;
        private IWallet _wallet;
        private IEnode _enode;
        private HiveRunner _hiveRunner;

        public const string DiscoveryNodesDbPath = "discoveryNodes";
        public const string PeersDbPath = "peers";

        public EthereumRunner(IRpcModuleProvider rpcModuleProvider, IConfigProvider configurationProvider, ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();
            
            InitRlp();
            _configProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _rpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _perfService = new PerfService(_logManager) {LogOnDebug = _initConfig.LogPerfStatsOnDebug};
            _networkHelper = new NetworkHelper(_logger);
        }

        public async Task Start()
        {
            if (_logger.IsInfo) _logger.Info("Initializing Ethereum");
            _runnerCancellation = new CancellationTokenSource();

            GenerateNodeKey();
            LoadChainSpec();
            UpdateNetworkConfig();
            await InitBlockchain();
            RegisterJsonRpcModules();
            if (HiveEnabled)
            {
                await InitHive();
            }
            if (_logger.IsDebug) _logger.Debug("Ethereum initialization completed");
        }

        private void InitRlp()
        {
            /* this is to invoke decoder registrations that happen in their static constructor
               looks like this will be quite a long term temporary solution (2018.11.27)*/
            ParityTraceDecoder.Init();
            NetworkNodeDecoder.Init();
        }

        private void GenerateNodeKey()
        {
// this is not secure at all but this is just the node key, nothing critical so far, will use the key store here later and allow to manage by password when launching the node
            if (_initConfig.TestNodeKey == null)
            {
                if (!File.Exists(UnsecuredNodeKeyFilePath))
                {
                    if (_logger.IsInfo)
                        _logger.Info("Generating private key for the node (no node key in configuration)");
                    _nodeKey = new PrivateKeyGenerator(_cryptoRandom).Generate();
                    File.WriteAllBytes(UnsecuredNodeKeyFilePath, _nodeKey.KeyBytes);
                }
                else
                {
                    _nodeKey = new PrivateKey(File.ReadAllBytes(UnsecuredNodeKeyFilePath));
                }
            }
            else
            {
                _nodeKey = new PrivateKey(_initConfig.TestNodeKey);
            }

            var ipVariable = Environment.GetEnvironmentVariable("NETHERMIND_ENODE_IPADDRESS");
            var localIp = string.IsNullOrWhiteSpace(ipVariable)
                ? _networkHelper.GetLocalIp()
                : IPAddress.Parse(ipVariable);

            _enode = new Enode(_nodeKey, localIp, _initConfig.P2PPort);
        }

        private void RegisterJsonRpcModules()
        {
            if (!_initConfig.JsonRpcEnabled)
            {
                return;
            }

            IReadOnlyDbProvider rpcDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            AlternativeChain rpcChain = new AlternativeChain(_blockTree, _blockValidator, _rewardCalculator, _specProvider, rpcDbProvider, _recoveryStep, _logManager, _transactionPool, _receiptStorage);

            ITracer tracer = new Tracer(rpcChain.Processor, _receiptStorage, _blockTree, _dbProvider.TraceDb);
            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, _blockProcessor, _transactionPool, _logManager);
            _wallet = HiveEnabled ? (IWallet)new HiveWallet() : new DevWallet(_logManager);
            RpcState rpcState = new RpcState(_blockTree, _specProvider, rpcDbProvider, _logManager);

            //creating blockchain bridge
            var blockchainBridge = new BlockchainBridge(
                _ethereumSigner,
                rpcState.StateProvider,
                rpcState.BlockTree,
                _transactionPool,
                _transactionPoolInfoProvider,
                _receiptStorage,
                filterStore,
                filterManager,
                _wallet,
                rpcState.TransactionProcessor);

            TransactionPool debugTransactionPool = new TransactionPool(new PersistentTransactionStorage(_dbProvider.PendingTxsDb, _specProvider),
                new PendingTransactionThresholdValidator(_initConfig.ObsoletePendingTransactionInterval, _initConfig.RemovePendingTransactionInterval),
                _timestamp,
                _ethereumSigner,
                _logManager,
                _initConfig.RemovePendingTransactionInterval,
                _initConfig.PeerNotificationThreshold);
            
            var debugReceiptStorage = new PersistentReceiptStorage(_dbProvider.ReceiptsDb, _specProvider);
            AlternativeChain debugChain = new AlternativeChain(_blockTree, _blockValidator, _rewardCalculator, _specProvider, rpcDbProvider, _recoveryStep, _logManager, debugTransactionPool, debugReceiptStorage);
            IReadOnlyDbProvider debugDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            var debugBridge = new DebugBridge(debugDbProvider, tracer, debugChain.Processor);

            JsonRpcModelMapper mapper = new JsonRpcModelMapper();

            EthModule module = new EthModule(_jsonSerializer, _configProvider, mapper, _logManager, blockchainBridge);
            _rpcModuleProvider.Register<IEthModule>(module);

            DebugModule debugModule = new DebugModule(_configProvider, _logManager, debugBridge, mapper, _jsonSerializer);
            _rpcModuleProvider.Register<IDebugModule>(debugModule);

            if (_sealEngine is CliqueSealEngine)
            {
                CliqueModule cliqueModule = new CliqueModule(_configProvider, _logManager, _jsonSerializer, new CliqueBridge(_blockProducer as CliqueBlockProducer, _blockTree));
                _rpcModuleProvider.Register<ICliqueModule>(cliqueModule);
            }

            AdminModule adminModule = new AdminModule(_configProvider, _logManager, _jsonSerializer);
            _rpcModuleProvider.Register<IAdminModule>(adminModule);

            TxPoolModule txPoolModule = new TxPoolModule(_configProvider, _logManager, _jsonSerializer, blockchainBridge, mapper);
            _rpcModuleProvider.Register<ITxPoolModule>(txPoolModule);

            if (_initConfig.NetworkEnabled && _initConfig.SynchronizationEnabled)
            {
                NetModule netModule = new NetModule(_configProvider, _logManager, _jsonSerializer, new NetBridge(_syncManager));
                _rpcModuleProvider.Register<INetModule>(netModule);
            }

            TraceModule traceModule = new TraceModule(_configProvider, _logManager, _jsonSerializer, tracer);
            _rpcModuleProvider.Register<ITraceModule>(traceModule);
            
            _rpcModuleProvider.Register<INethmModule>(new NethmModule(_configProvider, _logManager, _jsonSerializer, _enode));
        }

        private void UpdateNetworkConfig()
        {
            var localHost = _networkHelper.GetLocalIp()?.ToString() ?? "127.0.0.1";
            var networkConfig = _configProvider.GetConfig<INetworkConfig>();
            networkConfig.MasterExternalIp = localHost;
            networkConfig.MasterHost = localHost;
            networkConfig.BootNodes = _chainSpec.NetworkNodes.Select(nn => GetNode(nn, localHost)).ToArray();
            networkConfig.DbBasePath = _initConfig.BaseDbPath;
        }

        [Todo(Improve.Refactor, "Let us use same bootnode from chain spec for network and ethereum")]
        private ConfigNode GetNode(NetworkNode networkNode, string localHost)
        {
            var node = new ConfigNode
            {
                NodeId = networkNode.NodeId.PublicKey.ToString(false),
                Host = networkNode.Host == "127.0.0.1" ? localHost : networkNode.Host,
                Port = networkNode.Port,
                Description = networkNode.Description
            };
            return node;
        }

        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Shutting down...");
            _runnerCancellation.Cancel();

            if (_logger.IsInfo) _logger.Info("Stopping rlpx peer...");
            var rlpxPeerTask = _rlpxPeer?.Shutdown() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping peer manager...");
            var peerManagerTask = _peerManager?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping sync manager...");
            var syncManagerTask = _syncManager?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping block producer...");
            var blockProducerTask = _blockProducer?.StopAsync() ?? Task.CompletedTask;
            
            if (_logger.IsInfo) _logger.Info("Stopping blockchain processor...");
            var blockchainProcessorTask = (_blockchainProcessor?.StopAsync() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping discovery app...");
            var discoveryStopTask = _discoveryApp?.StopAsync() ?? Task.CompletedTask;

            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, syncManagerTask, blockchainProcessorTask, blockProducerTask);

            if (_logger.IsInfo) _logger.Info("Closing DBs...");
            _dbProvider.Dispose();
            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private void LoadChainSpec()
        {
            _logger.Info($"Loading chain spec from {_initConfig.ChainSpecPath}");
            ChainSpecLoader loader = new ChainSpecLoader(_jsonSerializer);
            _chainSpec = loader.LoadFromFile(_initConfig.ChainSpecPath);
            _chainSpec.NetworkNodes = _chainSpec.NetworkNodes.Where(n => !n.NodeId.PublicKey?.Equals(_nodeKey.PublicKey) ?? false).ToArray();
        }

        [Todo("This will be replaced with a bigger rewrite of state management so we can create a state at will")]
        private class AlternativeChain
        {
            public IBlockchainProcessor Processor { get; }
            public IStateProvider StateProvider { get; }

            public AlternativeChain(
                IBlockTree blockTree,
                IBlockValidator blockValidator,
                IRewardCalculator rewardCalculator,
                ISpecProvider specProvider,
                IReadOnlyDbProvider dbProvider,
                IBlockDataRecoveryStep recoveryStep,
                ILogManager logManager,
                ITransactionPool customTransactionPool,
                IReceiptStorage receiptStorage)
            {
                StateProvider = new StateProvider(new StateTree(dbProvider.StateDb), dbProvider.CodeDb, logManager);
                StorageProvider storageProvider = new StorageProvider(dbProvider.StateDb, StateProvider, logManager);
                IBlockTree readOnlyTree = new ReadOnlyBlockTree(blockTree);
                BlockhashProvider blockhashProvider = new BlockhashProvider(readOnlyTree);
                VirtualMachine virtualMachine = new VirtualMachine(StateProvider, storageProvider, blockhashProvider, logManager);
                ITransactionProcessor transactionProcessor = new TransactionProcessor(specProvider, StateProvider, storageProvider, virtualMachine, logManager);
                ITransactionPool transactionPool = customTransactionPool;
                IBlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, transactionProcessor, dbProvider.StateDb, dbProvider.CodeDb, dbProvider.TraceDb, StateProvider, storageProvider, transactionPool, receiptStorage, logManager);
                Processor = new BlockchainProcessor(readOnlyTree, blockProcessor, recoveryStep, logManager, false, false);
            }
        }

        private class RpcState
        {
            public IStateProvider StateProvider;
            public IStorageProvider StorageProvider;
            public IBlockhashProvider BlockhashProvider;
            public IVirtualMachine VirtualMachine;
            public TransactionProcessor TransactionProcessor;
            public IBlockTree BlockTree;

            public RpcState(IBlockTree blockTree, ISpecProvider specProvider, IReadOnlyDbProvider readOnlyDbProvider, ILogManager logManager)
            {
                ISnapshotableDb stateDb = readOnlyDbProvider.StateDb;
                IDb codeDb = readOnlyDbProvider.CodeDb;
                StateTree stateTree = new StateTree(readOnlyDbProvider.StateDb);

                StateProvider = new StateProvider(stateTree, codeDb, logManager);
                StorageProvider = new StorageProvider(stateDb, StateProvider, logManager);

                BlockTree = new ReadOnlyBlockTree(blockTree);
                BlockhashProvider = new BlockhashProvider(BlockTree);

                VirtualMachine = new VirtualMachine(StateProvider, StorageProvider, BlockhashProvider, logManager);
                TransactionProcessor = new TransactionProcessor(specProvider, StateProvider, StorageProvider, VirtualMachine, logManager);
            }
        }

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        private async Task InitBlockchain()
        {
            /* spec */
            if (_chainSpec.ChainId == RopstenSpecProvider.Instance.ChainId)
            {
                _specProvider = RopstenSpecProvider.Instance;
            }
            else if (_chainSpec.ChainId == MainNetSpecProvider.Instance.ChainId)
            {
                _specProvider = MainNetSpecProvider.Instance;
            }
            else if (_chainSpec.ChainId == RinkebySpecProvider.Instance.ChainId)
            {
                _specProvider = RinkebySpecProvider.Instance;
            }
            else if (_chainSpec.ChainId == GoerliSpecProvider.Instance.ChainId)
            {
                _specProvider = GoerliSpecProvider.Instance;
            }
            else if (_chainSpec.ChainId == SturebySpecProvider.Instance.ChainId)
            {
                _specProvider = SturebySpecProvider.Instance;
            }
            else
            {
                _specProvider = new SingleReleaseSpecProvider(LatestRelease.Instance, _chainSpec.ChainId);
            }

            /* sync */
            IDbConfig dbConfig = _configProvider.GetConfig<IDbConfig>();
            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                _logger.Info($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            _dbProvider = HiveEnabled
                ? (IDbProvider) new MemDbProvider()
                : new RocksDbProvider(_initConfig.BaseDbPath, dbConfig, _logManager);

            _ethereumSigner = new EthereumSigner(_specProvider, _logManager);
            _transactionPool = new TransactionPool(
                new PersistentTransactionStorage(_dbProvider.PendingTxsDb, _specProvider),
                new PendingTransactionThresholdValidator(_initConfig.ObsoletePendingTransactionInterval,
                    _initConfig.RemovePendingTransactionInterval), new Timestamp(),
                _ethereumSigner, _logManager, _initConfig.RemovePendingTransactionInterval,
                _initConfig.PeerNotificationThreshold);
            _receiptStorage = new PersistentReceiptStorage(_dbProvider.ReceiptsDb, _specProvider);

//            IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_dbBasePath, "debug"), dbConfig);
//            _dbProvider = new RpcDbProvider(_jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.NethVm1, _jsonSerializer, _logManager), _logManager, debugRecorder);

//            IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_dbBasePath, "debug"), dbConfig));
//            _dbProvider = debugReader;

            /* blockchain */
            _blockTree = new BlockTree(
                _dbProvider.BlocksDb,
                _dbProvider.BlockInfosDb,
                _specProvider,
                _transactionPool,
                _logManager);

            var cliqueConfig = new CliqueConfig(15, 30000);
            var clique = new CliqueSealEngine(cliqueConfig, _ethereumSigner, _nodeKey, _dbProvider.BlocksDb, _blockTree,
                _logManager);
            clique.CanSeal = _initConfig.IsMining;

            // TODO: read seal engine from ChainSpec
            _sealEngine =
                (_specProvider is MainNetSpecProvider) ? ConfigureSealEngine() :
                (_specProvider is RopstenSpecProvider) ? ConfigureSealEngine() :
                (_specProvider is SturebySpecProvider) ? ConfigureSealEngine() :
                (_specProvider is RinkebySpecProvider) ? clique :
                (_specProvider is GoerliSpecProvider) ? (ISealEngine) clique :
                NullSealEngine.Instance;

            _rewardCalculator = (_sealEngine is CliqueSealEngine)
                ? (IRewardCalculator) new NoBlockRewards()
                : new RewardCalculator(_specProvider);

            /* validation */
            var headerValidator = new HeaderValidator(
                _blockTree,
                _sealEngine,
                _specProvider,
                _logManager);

            var ommersValidator = new OmmersValidator(
                _blockTree,
                headerValidator,
                _logManager);

            var txValidator = new TransactionValidator(
                new SignatureValidator(_specProvider.ChainId));

            _blockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                ommersValidator,
                _specProvider,
                _logManager);

            var stateTree = new StateTree(_dbProvider.StateDb);

            var stateProvider = new StateProvider(
                stateTree,
                _dbProvider.CodeDb,
                _logManager);

            _stateProvider = stateProvider;

            var storageProvider = new StorageProvider(
                _dbProvider.StateDb,
                stateProvider,
                _logManager);

            _transactionPoolInfoProvider = new TransactionPoolInfoProvider(stateProvider);

            /* blockchain processing */
            var blockhashProvider = new BlockhashProvider(
                _blockTree);

            var virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                _logManager);

            var transactionProcessor = new TransactionProcessor(
                _specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                _logManager);

            var txRecoveryStep = new TxSignaturesRecoveryStep(_ethereumSigner, _transactionPool);
            _recoveryStep = _sealEngine is CliqueSealEngine
                ? new CompositeDataRecoveryStep(txRecoveryStep, new AuthorRecoveryStep(clique))
                : (IBlockDataRecoveryStep) txRecoveryStep;

            _blockProcessor = new BlockProcessor(
                _specProvider,
                _blockValidator,
                _rewardCalculator,
                transactionProcessor,
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                _dbProvider.TraceDb,
                stateProvider,
                storageProvider,
                _transactionPool,
                _receiptStorage,
                _logManager);

            _blockchainProcessor = new BlockchainProcessor(
                _blockTree,
                _blockProcessor,
                _recoveryStep,
                _logManager,
                true,
                true);

            // create shared objects between discovery and peer manager
            _nodeFactory = new NodeFactory(_logManager);
            _nodeStatsProvider =
                new NodeStatsProvider(_configProvider.GetConfig<IStatsConfig>(), _nodeFactory, _logManager);

            var jsonSerializer = new JsonSerializer(
                _logManager);

            var encrypter = new AesEncrypter(
                _configProvider,
                _logManager);

            _keyStore = new FileKeyStore(
                _configProvider,
                jsonSerializer,
                encrypter,
                _cryptoRandom,
                _logManager);

            if (_initConfig.IsMining)
            {
                IReadOnlyDbProvider minerDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
                AlternativeChain producerChain = new AlternativeChain(_blockTree, _blockValidator, _rewardCalculator,
                    _specProvider, minerDbProvider, _recoveryStep, _logManager, _transactionPool, _receiptStorage);

                if (_sealEngine is CliqueSealEngine engine)
                {
                    // TODO: need to introduce snapshot provider for clique and pass it here instead of CliqueSealEngine
                    if (_logger.IsWarn) _logger.Warn("Starting Clique block producer & sealer");
                    _blockProducer = new CliqueBlockProducer(_transactionPool, producerChain.Processor, _blockTree,
                        producerChain.StateProvider, _timestamp, _cryptoRandom, engine, cliqueConfig, _nodeKey.Address,
                        _logManager);
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn("Starting Dev block producer & sealer");
                    _blockProducer = new DevBlockProducer(_transactionPool, producerChain.Processor, _blockTree,
                        _timestamp, _logManager);
                }

                _blockProducer.Start();
            }

            if (!HiveEnabled)
            {
                _blockchainProcessor.Start();
                LoadGenesisBlock(_chainSpec,string.IsNullOrWhiteSpace(_initConfig.GenesisHash) ?
                        null : new Keccak(_initConfig.GenesisHash), _blockTree, stateProvider, _specProvider);
                if (_initConfig.ProcessingEnabled)
                {
#pragma warning disable 4014
                    LoadBlocksFromDb();
#pragma warning restore 4014
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn( $"Shutting down processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                    await _blockchainProcessor.StopAsync();
                }
            }

            await InitializeNetwork(
                _receiptStorage,
                headerValidator,
                txValidator);
        }

        private async Task LoadBlocksFromDb()
        {
            if (!_initConfig.SynchronizationEnabled)
            {
                return;
            }

            await _blockTree.LoadBlocksFromDb(_runnerCancellation.Token, null).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Loading blocks from DB failed.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsWarn) _logger.Warn("Loading blocks from DB canceled.");
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info("Loaded all blocks from DB");
                }
            });
        }

        private async Task InitializeNetwork(
            IReceiptStorage receiptStorage,
            IHeaderValidator headerValidator,
            TransactionValidator txValidator)
        {
            if (!_initConfig.NetworkEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping network init due to ({nameof(IInitConfig.NetworkEnabled)} set to false)");
                return;
            }

            _syncManager = new SynchronizationManager(
                _dbProvider.StateDb,
                _blockTree,
                _blockValidator,
                headerValidator,
                txValidator,
                _logManager,
                _configProvider.GetConfig<IBlockchainConfig>(),
                _perfService,
                receiptStorage);

            InitDiscovery();
            await InitPeer().ContinueWith(initPeerTask =>
            {
                if (initPeerTask.IsFaulted)
                {
                    _logger.Error("Unable to init peer manager.", initPeerTask.Exception);
                }
            });;

            await StartSync().ContinueWith(initNetTask =>
            {
                if (initNetTask.IsFaulted)
                {
                    _logger.Error("Unable to start sync.", initNetTask.Exception);
                }
            });

            await StartDiscovery().ContinueWith(initDiscoveryTask =>
            {
                if (initDiscoveryTask.IsFaulted)
                {
                    _logger.Error("Unable to start discovery protocol.", initDiscoveryTask.Exception);
                }
            });

            await StartPeer().ContinueWith(initPeerManagerTask =>
            {
                if (initPeerManagerTask.IsFaulted)
                {
                    _logger.Error("Unable to start peer manager.", initPeerManagerTask.Exception);
                }
            });

            if (_logger.IsInfo) _logger.Info($"Node is up and listening on {_enode.IpAddress}:{_enode.P2PPort}");
            if (_logger.IsInfo) _logger.Info($"{ClientVersion.Description}");
            if (_logger.IsInfo) _logger.Info(_enode.Info);
            if (_logger.IsInfo) _logger.Info($"enode address for test purposes: {_enode.Address}");
        }

        private ISealEngine ConfigureSealEngine()
        {
//            var sealEngine = NullSealEngine.Instance;
            var difficultyCalculator = new DifficultyCalculator(_specProvider);
            var sealEngine = new EthashSealEngine(new Ethash(_logManager), difficultyCalculator, _logManager);

//            var blockMiningTime = TimeSpan.FromMilliseconds(_initConfig.FakeMiningDelay);
//            var sealEngine = new FakeSealEngine(blockMiningTime, false);
//            sealEngine.IsMining = _initConfig.IsMining;
//            if (sealEngine.IsMining)
//            {
//                var transactionDelay = TimeSpan.FromMilliseconds(_initConfig.FakeMiningDelay / 4);
//                TestTransactionsGenerator testTransactionsGenerator =
//                    new TestTransactionsGenerator(transactionStore, ethereumSigner, transactionDelay, _logManager);
//                // stateProvider.CreateAccount(testTransactionsGenerator.SenderAddress, 1000.Ether());
            return sealEngine;
        }

        private static void LoadGenesisBlock(
            ChainSpec chainSpec,
            Keccak expectedGenesisHash,
            IBlockTree blockTree,
            IStateProvider stateProvider,
            ISpecProvider specProvider)
        {
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (blockTree.Genesis != null)
            {
                return;
            }

            foreach (KeyValuePair<Address, UInt256> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }

            stateProvider.Commit(specProvider.GenesisSpec);

            Block genesis = chainSpec.Genesis;
            genesis.StateRoot = stateProvider.StateRoot;
            genesis.Hash = BlockHeader.CalculateHash(genesis.Header);

            ManualResetEvent genesisProcessedEvent = new ManualResetEvent(false);

            bool genesisLoaded = false;

            void GenesisProcessed(object sender, BlockEventArgs args)
            {
                genesisLoaded = true;
                blockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            blockTree.NewHeadBlock += GenesisProcessed;
            blockTree.SuggestBlock(genesis);
            genesisProcessedEvent.WaitOne(TimeSpan.FromSeconds(5));
            if (!genesisLoaded)
            {
                throw new BlockchainException("Genesis block processing failure");
            }

            // if expectedGenesisHash is null here then it means that we do not care about the exact value in advance (e.g. in test scenarios)
            if (expectedGenesisHash != null && blockTree.Genesis.Hash != expectedGenesisHash)
            {
                throw new Exception($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {blockTree.Genesis.Hash}");
            }
        }

        private Task StartSync()
        {
            if (!_initConfig.SynchronizationEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping blockchain synchronization init due to ({nameof(IInitConfig.SynchronizationEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_blockTree.Head.ToString(BlockHeader.Format.Short)}.");

            _syncManager.Start();
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            /* rlpx */
            var eciesCipher = new EciesCipher(_cryptoRandom);
            var eip8Pad = new Eip8MessagePad(_cryptoRandom);
            _messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            var encryptionHandshakeServiceA = new EncryptionHandshakeService(_messageSerializationService, eciesCipher,
                _cryptoRandom, new Signer(), _nodeKey, _logManager);

            /* p2p */
            _messageSerializationService.Register(new HelloMessageSerializer());
            _messageSerializationService.Register(new DisconnectMessageSerializer());
            _messageSerializationService.Register(new PingMessageSerializer());
            _messageSerializationService.Register(new PongMessageSerializer());

            /* eth62 */
            _messageSerializationService.Register(new StatusMessageSerializer());
            _messageSerializationService.Register(new TransactionsMessageSerializer());
            _messageSerializationService.Register(new GetBlockHeadersMessageSerializer());
            _messageSerializationService.Register(new NewBlockHashesMessageSerializer());
            _messageSerializationService.Register(new GetBlockBodiesMessageSerializer());
            _messageSerializationService.Register(new BlockHeadersMessageSerializer());
            _messageSerializationService.Register(new BlockBodiesMessageSerializer());
            _messageSerializationService.Register(new NewBlockMessageSerializer());

            /* eth63 */
            _messageSerializationService.Register(new GetNodeDataMessageSerializer());
            _messageSerializationService.Register(new NodeDataMessageSerializer());
            _messageSerializationService.Register(new GetReceiptsMessageSerializer());
            _messageSerializationService.Register(new ReceiptsMessageSerializer());

            _rlpxPeer = new RlpxPeer(new NodeId(_nodeKey.PublicKey), _initConfig.P2PPort,
                _syncManager,
                _messageSerializationService,
                encryptionHandshakeServiceA,
                _nodeStatsProvider,
                _logManager, _perfService,
                _blockTree, _transactionPool);

            await _rlpxPeer.Init();

            var peerStorage = new NetworkStorage(PeersDbPath, _configProvider.GetConfig<INetworkConfig>(), _logManager, _perfService);
            _peerManager = new PeerManager(_rlpxPeer, _discoveryApp, _syncManager, _nodeStatsProvider, peerStorage,
                _nodeFactory, _configProvider, _perfService, _transactionPool, _logManager);
            _peerManager.Init(_initConfig.DiscoveryEnabled);
        }

        private async Task StartPeer()
        {
            if (!_initConfig.PeerManagerEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(_initConfig.PeerManagerEnabled)} set to false)");
                return;
            }

            if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
            await _peerManager.Start();
            if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            _configProvider.GetConfig<INetworkConfig>().MasterPort = _initConfig.DiscoveryPort;

            var privateKeyProvider = new SameKeyGenerator(_nodeKey);
            var discoveryMessageFactory = new DiscoveryMessageFactory(_configProvider, _timestamp);
            var nodeIdResolver = new NodeIdResolver(_signer);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _messageSerializationService,
                _signer,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver,
                _nodeFactory);

            msgSerializersProvider.RegisterDiscoverySerializers();

            var nodeDistanceCalculator = new NodeDistanceCalculator(_configProvider);

            var nodeTable = new NodeTable(
                _nodeFactory,
                _keyStore,
                nodeDistanceCalculator,
                _configProvider,
                _logManager);

            var evictionManager = new EvictionManager(
                nodeTable,
                _logManager);

            var nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                _nodeFactory,
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _nodeStatsProvider,
                _configProvider,
                _logManager);

            var discoveryStorage = new NetworkStorage(
                DiscoveryNodesDbPath,
                _configProvider.GetConfig<INetworkConfig>(),
                _logManager,
                _perfService);

            _discoveryManager = new DiscoveryManager(
                nodeLifeCycleFactory,
                _nodeFactory,
                nodeTable,
                discoveryStorage,
                _configProvider,
                _logManager);

            var nodesLocator = new NodesLocator(
                nodeTable,
                _discoveryManager,
                _configProvider,
                _logManager);

            _discoveryApp = new DiscoveryApp(
                nodesLocator,
                _discoveryManager,
                _nodeFactory,
                nodeTable,
                _messageSerializationService,
                _cryptoRandom,
                discoveryStorage,
                _configProvider,
                _logManager, _perfService);

            _discoveryApp.Initialize(_nodeKey.PublicKey);
        }

        private Task StartDiscovery()
        {
            if (!_initConfig.DiscoveryEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to ({nameof(IInitConfig.DiscoveryEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
            _discoveryApp.Start();
            if (_logger.IsDebug) _logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }

        private async Task InitHive()
        {
            if (_logger.IsInfo) _logger.Info("Initializing Hive");
            _hiveRunner = new HiveRunner(_jsonSerializer, _blockchainProcessor, _blockTree as BlockTree,
                _stateProvider, _dbProvider.StateDb, _logger, _configProvider, _specProvider, _wallet as HiveWallet);
            await _hiveRunner.Start();
        }
    }
}