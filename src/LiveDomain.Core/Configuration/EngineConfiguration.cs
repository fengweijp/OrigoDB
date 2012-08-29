﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using LiveDomain.Core.Logging;
using LiveDomain.Core.Security;
using TinyIoC;

namespace LiveDomain.Core
{

    public partial class EngineConfiguration : ConfigurationBase
    {
        protected TinyIoCContainer _registry;

        /// <summary>
        /// Ensure command journal is created only once
        /// </summary>
        private bool _commandJournalCreated;

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        public const string DefaultDateFormatString = "yyyy.MM.dd.hh.mm.ss.fff";
        public const long DefaultJournalSegmentSizeInBytes = 1024 * 1024;


        /// <summary>
        /// When segment exceeds this size a new segment is created
        /// </summary>
        public long JournalSegmentSizeInBytes { get; set; }


        /// <summary>
        /// Make a deep copy of all command and query results so no references are passed out from the model. Default is true.
        /// <remarks>
        /// Set to false if you are certain that results will not be modified by client code. Note also that 
        /// the state of the resultset can be modified by a subsequent command rendering the result graph inconsistent.</remarks>
        /// </summary>
        /// 
        public bool CloneResults { get; set; }

        /// <summary>
        /// Make a deep copy of each command prior to execution. This will force a fast 
        /// failure of commands that wont serialize.
        /// </summary>
        public bool CloneCommands { get; set; }


        /// <summary>
        /// Maximum time to wait for any read or write lock
        /// </summary>
        public TimeSpan LockTimeout { get; set; }


        /// <summary>
        /// When to take automatic snapshots
        /// </summary>
        public SnapshotBehavior SnapshotBehavior { get; set; }

        public StorageType StorageType { get; set; }

        /// <summary>
        /// Effects which ISynchronizer is chosen by CreateSynchronizer()
        /// </summary>
        public SynchronizationMode Synchronization { get; set; }

        /// <summary>
        /// Serialization format, defaults to BinaryFormatter
        /// </summary>
        public ObjectFormatting ObjectFormatting { get; set; }

        /// <summary>
        /// Synchronous or Asynchronous journaling
        /// </summary>
        public JournalWriterMode JournalWriterMode { get; set; }


        /// <summary>
        /// Create an EngineConfiguration instance using default values
        /// </summary>
        /// <param name="targetLocation"></param>
        public EngineConfiguration(string targetLocation = null)
        {

            Location = targetLocation;

            //Set default values
            LockTimeout = DefaultTimeout;
            Synchronization = SynchronizationMode.ReadWrite;
            ObjectFormatting = ObjectFormatting.NetBinaryFormatter;
            JournalWriterMode = JournalWriterMode.Synchronous;
            StorageType = StorageType.FileSystem;
            JournalSegmentSizeInBytes = DefaultJournalSegmentSizeInBytes;
            CloneResults = true;
            CloneCommands = true;

            _registry = new TinyIoCContainer();
            _registry.Register<ICommandJournal>((c, p) => new CommandJournal(this));
            _registry.Register<IAuthorizer<Type>>((c, p) => new TypeBasedPermissionSet(Permission.Allowed));
            _registry.Register<ISerializer>((c,p) => new Serializer(CreateFormatter()));

            InitSynchronizers();
            InitJournalWriters();
            InitStorageTypes();
            InitFormatters();
        }

        #region Factory initializers
        /// <summary>
        /// Created a named registration for each SynchronizationMode enumeration value
        /// </summary>
        private void InitSynchronizers()
        {
            _registry.Register<ISynchronizer>((c, p) => new ReadWriteSynchronizer(LockTimeout),
                                              SynchronizationMode.ReadWrite.ToString());
            _registry.Register<ISynchronizer>((c, p) => new NullSynchronizer(),
                                              SynchronizationMode.None.ToString());
            _registry.Register<ISynchronizer>((c, p) => new ExclusiveSynchronizer(LockTimeout),
                                  SynchronizationMode.Exclusive.ToString());


        }

        /// <summary>
        /// Created a named registration for each JournalWriterPerformance enumeration value
        /// </summary>
        private void InitJournalWriters()
        {
            _registry.Register<IJournalWriter>(
                (c, p) => new AsynchronousJournalWriter(new SynchronousJournalWriter(p["stream"] as Stream, CreateSerializer())),
                JournalWriterMode.Asynchronous.ToString());
            _registry.Register<IJournalWriter>(
                (c, p) => new SynchronousJournalWriter(p["stream"] as Stream, CreateSerializer()),
                JournalWriterMode.Synchronous.ToString());
        }

        private void InitFormatters()
        {
            _registry.Register<IFormatter>((c, p) => new BinaryFormatter(),
                                           ObjectFormatting.NetBinaryFormatter.ToString());
            _registry.Register<IFormatter>((c, p) => LoadFromConfig<IFormatter>(),
                                           ObjectFormatting.Custom.ToString());
        }


        /// <summary>
        /// Create a named registration for each StorageMode enumeration value
        /// </summary>
        private void InitStorageTypes()
        {

            _registry.Register<IStorage>((c, p) => new FileStorage(this), StorageType.FileSystem.ToString());
            _registry.Register<IStorage, NullStorage>(StorageType.None.ToString());

            //If StorageMode is set to custom and no factory has been injected, the fully qualified type 
            //name will be resolved from the app configuration file.
            _registry.Register<IStorage>((c, p) => LoadFromConfig<IStorage>(), StorageType.Custom.ToString());
        } 
        #endregion

        /// <summary>
        /// Looks up a custom implementation in app config file with the key LiveDb.EngineConfiguration
        /// </summary>
        /// <returns></returns>
        public static EngineConfiguration Create()
        {
            //we need an instance to be able to call an instance method
            var bootloader = new EngineConfiguration();

            //look for specific implementation in config file, otherwise return self
            return bootloader.LoadFromConfigOrDefault(() => bootloader);
        }

        #region Factory methods
        protected internal virtual ISerializer CreateSerializer()
        {
            return _registry.Resolve<ISerializer>();
        }

        protected internal virtual IFormatter CreateFormatter()
        {
            string name = ObjectFormatting.ToString();
            return _registry.Resolve<IFormatter>(name);
        }

        /// <summary>
        /// Gets a synchronizer based on the SynchronizationMode property
        /// </summary>
        /// <returns></returns>
        protected internal virtual ISynchronizer CreateSynchronizer()
        {
            string registrationName = Synchronization.ToString();
            return _registry.Resolve<ISynchronizer>(registrationName);
        }

        protected internal virtual IJournalWriter CreateJournalWriter(Stream stream)
        {
            string registrationName = JournalWriterMode.ToString();
            var args = new NamedParameterOverloads { { "stream", stream } };
            return _registry.Resolve<IJournalWriter>(registrationName, args);
        }


        protected internal virtual IAuthorizer<Type> CreateAuthorizer()
        {
            return _registry.Resolve<IAuthorizer<Type>>();
        }

        protected internal virtual IStorage CreateStorage()
        {
            string name = StorageType.ToString();
            return _registry.Resolve<IStorage>(name);
        }

        /// <summary>
        /// Creates and returns a new command journal instance, can only be called once.
        /// The default type is CommandJournal unless a custom factory has been set by 
        /// calling SetCommandJournalFactory()
        /// </summary>
        /// <returns></returns>
        protected internal virtual ICommandJournal CreateCommandJournal()
        {
            if (_commandJournalCreated) throw new InvalidOperationException();
            _commandJournalCreated = true;
            return _registry.Resolve<ICommandJournal>();
        }

        #endregion

        #region Factory Injection Methods
        /// <summary>
        /// Inject a custom command journal factory. 
        /// Will throw if called after journal has been created.
        /// </summary>
        /// <param name="factory"></param>
        public void SetCommandJournalFactory(Func<EngineConfiguration, ICommandJournal> factory)
        {
            if (_commandJournalCreated) throw new InvalidOperationException();
            _registry.Register<ICommandJournal>((c, p) => factory.Invoke(this));
        }

        public void SetSynchronizerFactory(Func<EngineConfiguration, ISynchronizer> factory)
        {
            Synchronization = SynchronizationMode.Custom;
            string registrationName = Synchronization.ToString();
            _registry.Register<ISynchronizer>((c, p) => factory.Invoke(this), registrationName);
        }

        public void SetAuthorizerFactory(Func<EngineConfiguration, IAuthorizer<Type>> factory)
        {
            _registry.Register<IAuthorizer<Type>>((c, p) => factory.Invoke(this));
        }

        public void SetFormatterFactory(Func<EngineConfiguration, IFormatter> factory)
        {
            ObjectFormatting = ObjectFormatting.Custom;
            string registrationName = ObjectFormatting.ToString();
            _registry.Register<IFormatter>((c, p) => factory.Invoke(this), registrationName);
        }

        /// <summary>
        /// Inject your custom storage factory here. StorageMode property will be set to Custom
        /// </summary>
        /// <param name="factory"></param>
        public void SetStorageFactory(Func<EngineConfiguration, IStorage> factory)
        {
            StorageType = StorageType.Custom;
            string registrationName = StorageType.ToString();
            _registry.Register<IStorage>((c, p) => factory.Invoke(this), registrationName);
        }

        public void SetSerializerFactory(Func<EngineConfiguration, ISerializer> factory)
        {
            _registry.Register<ISerializer>((c, p) => factory.Invoke(this));
        } 
        #endregion
    }
}
