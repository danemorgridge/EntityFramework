// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Core.Objects
{
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Configuration;
    using System.Data.Common;
    using System.Data.Entity.Config;
    using System.Data.Entity.Core.Common;
    using System.Data.Entity.Core.Common.CommandTrees;
    using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;
    using System.Data.Entity.Core.Common.Internal.Materialization;
    using System.Data.Entity.Core.Common.Utils;
    using System.Data.Entity.Core.EntityClient;
    using System.Data.Entity.Core.EntityClient.Internal;
    using System.Data.Entity.Core.Metadata.Edm;
    using System.Data.Entity.Core.Objects.DataClasses;
    using System.Data.Entity.Core.Objects.ELinq;
    using System.Data.Entity.Core.Objects.Internal;
    using System.Data.Entity.Core.Query.InternalTrees;
    using System.Data.Entity.Infrastructure;
    using System.Data.Entity.Internal;
    using System.Data.Entity.Resources;
    using System.Data.Entity.Utilities;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.Versioning;
    using System.Text;
#if !NET40
    using System.Threading;
    using System.Threading.Tasks;
#endif
    using System.Transactions;

    /// <summary>
    ///     ObjectContext is the top-level object that encapsulates a connection between the CLR and the database,
    ///     serving as a gateway for Create, Read, Update, and Delete operations.
    /// </summary>
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
    public class ObjectContext : IDisposable, IObjectContextAdapter
    {
        #region Fields

        private bool _disposed;
        private IEntityAdapter _adapter;

        // Connection may be null if used by ObjectMaterializer for detached ObjectContext,
        // but those code paths should not touch the connection.
        //
        // If the connection is null, this indicates that this object has been disposed.
        // Disposal for this class doesn't mean complete disposal, 
        // but rather the disposal of the underlying connection object if the ObjectContext owns the connection,
        // or the separation of the underlying connection object from the ObjectContext if the ObjectContext does not own the connection.
        //
        // Operations that require a connection should throw an ObjectDiposedException if the connection is null.
        // Other operations that do not need a connection should continue to work after disposal.
        private EntityConnection _connection;

        private readonly MetadataWorkspace _workspace;
        private ObjectStateManager _objectStateManager;
        private ClrPerspective _perspective;
        private readonly bool _createdConnection;
        private bool _openedConnection; // whether or not the context opened the connection to do an operation
        private int _connectionRequestCount; // the number of active requests for an open connection
        private int? _queryTimeout;
        private Transaction _lastTransaction;

        private readonly bool _disallowSettingDefaultContainerName;

        private EventHandler _onSavingChanges;

        private ObjectMaterializedEventHandler _onObjectMaterialized;

        private ObjectQueryProvider _queryProvider;

        private readonly EntityWrapperFactory _entityWrapperFactory;
        private readonly ObjectQueryExecutionPlanFactory _objectQueryExecutionPlanFactory;
        private readonly Translator _translator;
        private readonly ColumnMapFactory _columnMapFactory;

        private readonly ObjectContextOptions _options = new ObjectContextOptions();

        private readonly IDbCommandInterceptor _commandInterceptor;

        private const string UseLegacyPreserveChangesBehavior = "EntityFramework_UseLegacyPreserveChangesBehavior";

        #endregion Fields

        #region Constructors

        /// <summary>
        ///     Creates an ObjectContext with the given connection and metadata workspace.
        /// </summary>
        /// <param name="connection"> connection to the store </param>
        public ObjectContext(EntityConnection connection)
            : this(connection, true, null)
        {
            _createdConnection = false;
        }

        /// <summary>
        ///     Creates an ObjectContext with the given connection and metadata workspace.
        /// </summary>
        /// <param name="connection"> connection to the store </param>
        /// <param name="contextOwnsConnection"> If set to true the connection is disposed when the context is disposed, otherwise the caller must dispose the connection. </param>
        public ObjectContext(EntityConnection connection, bool contextOwnsConnection)
            : this(connection, true, null)
        {
            _createdConnection = contextOwnsConnection;
        }

        /// <summary>
        ///     Creates an ObjectContext with the given connection string and
        ///     default entity container name.  This constructor
        ///     creates and initializes an EntityConnection so that the context is
        ///     ready to use; no other initialization is necessary.  The given
        ///     connection string must be valid for an EntityConnection; connection
        ///     strings for other connection types are not supported.
        /// </summary>
        /// <param name="connectionString"> the connection string to use in the underlying EntityConnection to the store </param>
        /// <exception cref="ArgumentNullException">connectionString is null</exception>
        /// <exception cref="ArgumentException">if connectionString is invalid</exception>
        [ResourceExposure(ResourceScope.Machine)] //Exposes the file names as part of ConnectionString which are a Machine resource
        [ResourceConsumption(ResourceScope.Machine)] //For CreateEntityConnection method. But the paths are not created in this method.
        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope",
            Justification = "Object is in fact passed to property of the class and gets Disposed properly in the Dispose() method.")]
        public ObjectContext(string connectionString)
            : this(CreateEntityConnection(connectionString), false, null)
        {
            _createdConnection = true;
        }

        /// <summary>
        ///     Creates an ObjectContext with the given connection string and
        ///     default entity container name.  This protected constructor creates and initializes an EntityConnection so that the context
        ///     is ready to use; no other initialization is necessary.  The given connection string must be valid for an EntityConnection;
        ///     connection strings for other connection types are not supported.
        /// </summary>
        /// <param name="connectionString"> the connection string to use in the underlying EntityConnection to the store </param>
        /// <param name="defaultContainerName"> the name of the default entity container </param>
        /// <exception cref="ArgumentNullException">connectionString is null</exception>
        /// <exception cref="ArgumentException">either connectionString or defaultContainerName is invalid</exception>
        [ResourceExposure(ResourceScope.Machine)] //Exposes the file names as part of ConnectionString which are a Machine resource
        [ResourceConsumption(ResourceScope.Machine)] //For ObjectContext method. But the paths are not created in this method.
        [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors",
            Justification = "Class is internal and methods are made virtual for testing purposes only. They cannot be overrided by user.")]
        protected ObjectContext(string connectionString, string defaultContainerName)
            : this(connectionString)
        {
            DefaultContainerName = defaultContainerName;
            if (!string.IsNullOrEmpty(defaultContainerName))
            {
                _disallowSettingDefaultContainerName = true;
            }
        }

        /// <summary>
        ///     Creates an ObjectContext with the given connection and metadata workspace.
        /// </summary>
        /// <param name="connection"> connection to the store </param>
        /// <param name="defaultContainerName"> the name of the default entity container </param>
        [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors",
            Justification = "Class is internal and methods are made virtual for testing purposes only. They cannot be overrided by user.")]
        protected ObjectContext(EntityConnection connection, string defaultContainerName)
            : this(connection)
        {
            DefaultContainerName = defaultContainerName;
            if (!string.IsNullOrEmpty(defaultContainerName))
            {
                _disallowSettingDefaultContainerName = true;
            }
        }

        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly")]
        [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors",
            Justification = "Class is internal and methods are made virtual for testing purposes only. They cannot be overrided by user.")]
        internal ObjectContext(
            EntityConnection connection,
            bool isConnectionConstructor,
            ObjectQueryExecutionPlanFactory objectQueryExecutionPlanFactory,
            Translator translator = null,
            ColumnMapFactory columnMapFactory = null,
            IDbCommandInterceptor commandInterceptor = null)
        {
            Check.NotNull(connection, "connection");

            _objectQueryExecutionPlanFactory = objectQueryExecutionPlanFactory ?? new ObjectQueryExecutionPlanFactory();
            _translator = translator ?? new Translator();
            _columnMapFactory = columnMapFactory ?? new ColumnMapFactory();

            _connection = connection;
            _connection.StateChange += ConnectionStateChange;
            _entityWrapperFactory = new EntityWrapperFactory();
            // Ensure a valid connection
            var connectionString = connection.ConnectionString;
            if (connectionString == null
                || connectionString.Trim().Length == 0)
            {
                throw isConnectionConstructor
                          ? new ArgumentException(Strings.ObjectContext_InvalidConnection, "connection", null)
                          : new ArgumentException(Strings.ObjectContext_InvalidConnectionString, "connectionString", null);
            }

            try
            {
                _workspace = RetrieveMetadataWorkspaceFromConnection();
            }
            catch (InvalidOperationException e)
            {
                // Intercept exceptions retrieving workspace, and wrap exception in appropriate
                // message based on which constructor pattern is being used.
                throw isConnectionConstructor
                          ? new ArgumentException(Strings.ObjectContext_InvalidConnection, "connection", e)
                          : new ArgumentException(Strings.ObjectContext_InvalidConnectionString, "connectionString", e);
            }

            Debug.Assert(_workspace != null);

            // load config file properties
            var value = ConfigurationManager.AppSettings[UseLegacyPreserveChangesBehavior];
            var useV35Behavior = false;
            if (Boolean.TryParse(value, out useV35Behavior))
            {
                ContextOptions.UseLegacyPreserveChangesBehavior = useV35Behavior;
            }

            _commandInterceptor
                = commandInterceptor
                  ?? DbConfiguration.GetService<IDbCommandInterceptor>();
        }

        /// <summary>
        ///     For testing porpoises only.
        /// </summary>
        internal ObjectContext(
            ObjectQueryExecutionPlanFactory objectQueryExecutionPlanFactory = null,
            Translator translator = null,
            ColumnMapFactory columnMapFactory = null,
            IDbCommandInterceptor commandInterceptor = null,
            IEntityAdapter adapter = null)
        {
            _objectQueryExecutionPlanFactory = objectQueryExecutionPlanFactory ?? new ObjectQueryExecutionPlanFactory();
            _translator = translator ?? new Translator();
            _columnMapFactory = columnMapFactory ?? new ColumnMapFactory();
            _commandInterceptor = commandInterceptor;
            _adapter = adapter;
        }

        #endregion //Constructors

        #region Properties

        /// <summary>
        ///     Gets the connection to the store.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        ///     If the
        ///     <see cref="ObjectContext" />
        ///     instance has been disposed.
        /// </exception>
        public virtual DbConnection Connection
        {
            get
            {
                if (_connection == null)
                {
                    throw new ObjectDisposedException(null, Strings.ObjectContext_ObjectDisposed);
                }

                return _connection;
            }
        }

        /// <summary>
        ///     Gets or sets the default container name.
        /// </summary>
        public virtual string DefaultContainerName
        {
            get
            {
                var container = Perspective.GetDefaultContainer();
                return ((null != container) ? container.Name : String.Empty);
            }
            set
            {
                if (!_disallowSettingDefaultContainerName)
                {
                    Perspective.SetDefaultContainer(value);
                }
                else
                {
                    throw new InvalidOperationException(Strings.ObjectContext_CannotSetDefaultContainerName);
                }
            }
        }

        /// <summary>
        ///     Gets the metadata workspace associated with this ObjectContext.
        /// </summary>
        public virtual MetadataWorkspace MetadataWorkspace
        {
            get { return _workspace; }
        }

        /// <summary>
        ///     Gets the ObjectStateManager used by this ObjectContext.
        /// </summary>
        public virtual ObjectStateManager ObjectStateManager
        {
            get
            {
                if (_objectStateManager == null)
                {
                    _objectStateManager = new ObjectStateManager(_workspace);
                }

                return _objectStateManager;
            }
        }

        /// <summary>
        ///     ClrPerspective based on the MetadataWorkspace.
        /// </summary>
        internal ClrPerspective Perspective
        {
            get
            {
                if (_perspective == null)
                {
                    _perspective = new ClrPerspective(MetadataWorkspace);
                }

                return _perspective;
            }
        }

        /// <summary>
        ///     Gets and sets the timeout value used for queries with this ObjectContext.
        ///     A null value indicates that the default value of the underlying provider
        ///     will be used.
        /// </summary>
        public virtual int? CommandTimeout
        {
            get { return _queryTimeout; }
            set
            {
                if (value.HasValue
                    && value < 0)
                {
                    throw new ArgumentException(Strings.ObjectContext_InvalidCommandTimeout, "value");
                }

                _queryTimeout = value;
            }
        }

        /// <summary>
        ///     Gets the LINQ query provider associated with this object context.
        /// </summary>
        protected internal virtual IQueryProvider QueryProvider
        {
            get
            {
                if (null == _queryProvider)
                {
                    _queryProvider = new ObjectQueryProvider(this);
                }

                return _queryProvider;
            }
        }

        /// <summary>
        ///     Whether or not we are in the middle of materialization
        ///     Used to suppress operations such as lazy loading that are not allowed during materialization
        /// </summary>
        internal bool InMaterialization { get; set; }

        /// <summary>
        ///     Get <see cref="ObjectContextOptions" /> instance that contains options
        ///     that affect the behavior of the ObjectContext.
        /// </summary>
        /// <value>
        ///     Instance of <see cref="ObjectContextOptions" /> for the current ObjectContext. This value will never be null.
        /// </value>
        public virtual ObjectContextOptions ContextOptions
        {
            get { return _options; }
        }

        internal CollectionColumnMap ColumnMapBuilder { get; set; }

        internal virtual EntityWrapperFactory EntityWrapperFactory
        {
            get { return _entityWrapperFactory; }
        }

        /// <summary>
        ///     Returns itself. ObjectContext implements <see cref="IObjectContextAdapter" /> to provide a common
        ///     interface for <see cref="DbContext" /> and ObjectContext both of which will return the underlying
        ///     ObjectContext.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        ObjectContext IObjectContextAdapter.ObjectContext
        {
            get { return this; }
        }

        #endregion //Properties

        #region Events

        /// <summary>
        ///     Property for adding a delegate to the SavingChanges Event.
        /// </summary>
        public event EventHandler SavingChanges
        {
            add { _onSavingChanges += value; }
            remove { _onSavingChanges -= value; }
        }

        /// <summary>
        ///     A private helper function for the _savingChanges/SavingChanges event.
        /// </summary>
        private void OnSavingChanges()
        {
            if (null != _onSavingChanges)
            {
                _onSavingChanges(this, new EventArgs());
            }
        }

        /// <summary>
        ///     Event raised when a new entity object is materialized.  That is, the event is raised when
        ///     a new entity object is created from data in the store as part of a query or load operation.
        /// </summary>
        /// <remarks>
        ///     Note that the event is raised after included (spanned) referenced objects are loaded, but
        ///     before included (spanned) collections are loaded.  Also, for independent associations,
        ///     any stub entities for related objects that have not been loaded will also be created before
        ///     the event is raised.
        ///     It is possible for an entity object to be created and then thrown away if it is determined
        ///     that an entity with the same ID already exists in the Context.  This event is not raised
        ///     in those cases.
        /// </remarks>
        public event ObjectMaterializedEventHandler ObjectMaterialized
        {
            add { _onObjectMaterialized += value; }
            remove { _onObjectMaterialized -= value; }
        }

        internal void OnObjectMaterialized(object entity)
        {
            if (null != _onObjectMaterialized)
            {
                _onObjectMaterialized(this, new ObjectMaterializedEventArgs(entity));
            }
        }

        /// <summary>
        ///     Returns true if any handlers for the ObjectMaterialized event exist.  This is
        ///     used for perf reasons to avoid collecting the information needed for the event
        ///     if there is no point in firing it.
        /// </summary>
        internal bool OnMaterializedHasHandlers
        {
            get { return _onObjectMaterialized != null && _onObjectMaterialized.GetInvocationList().Length != 0; }
        }

        #endregion //Events

        #region Methods

        /// <summary>
        ///     AcceptChanges on all associated entries in the ObjectStateManager so their resultant state is either unchanged or detached.
        /// </summary>
        public virtual void AcceptAllChanges()
        {
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();

            if (ObjectStateManager.SomeEntryWithConceptualNullExists())
            {
                throw new InvalidOperationException(Strings.ObjectContext_CommitWithConceptualNull);
            }

            // There are scenarios in which order of calling AcceptChanges does matter:
            // in case there is an entity in Deleted state and another entity in Added state with the same ID -
            // it is necessary to call AcceptChanges on Deleted entity before calling AcceptChanges on Added entity
            // (doing this in the other order there is conflict of keys).
            foreach (var entry in ObjectStateManager.GetObjectStateEntries(EntityState.Deleted))
            {
                entry.AcceptChanges();
            }

            foreach (var entry in ObjectStateManager.GetObjectStateEntries(EntityState.Added | EntityState.Modified))
            {
                entry.AcceptChanges();
            }

            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
        }

        private void VerifyRootForAdd(
            bool doAttach, string entitySetName, IEntityWrapper wrappedEntity, EntityEntry existingEntry, out EntitySet entitySet,
            out bool isNoOperation)
        {
            isNoOperation = false;

            EntitySet entitySetFromName = null;

            if (doAttach)
            {
                // For AttachTo the entity set name is optional
                if (!String.IsNullOrEmpty(entitySetName))
                {
                    entitySetFromName = GetEntitySetFromName(entitySetName);
                }
            }
            else
            {
                // For AddObject the entity set name is obligatory
                entitySetFromName = GetEntitySetFromName(entitySetName);
            }

            // Find entity set using entity key
            EntitySet entitySetFromKey = null;

            var key = existingEntry != null ? existingEntry.EntityKey : wrappedEntity.GetEntityKeyFromEntity();
            if (null != (object)key)
            {
                entitySetFromKey = key.GetEntitySet(MetadataWorkspace);

                if (entitySetFromName != null)
                {
                    // both entity sets are not null, compare them
                    EntityUtil.ValidateEntitySetInKey(key, entitySetFromName, "entitySetName");
                }
                key.ValidateEntityKey(_workspace, entitySetFromKey);
            }

            entitySet = entitySetFromKey ?? entitySetFromName;

            // Check if entity set was found
            if (entitySet == null)
            {
                throw new InvalidOperationException(Strings.ObjectContext_EntitySetNameOrEntityKeyRequired);
            }

            ValidateEntitySet(entitySet, wrappedEntity.IdentityType);

            // If in the middle of Attach, try to find the entry by key
            if (doAttach && existingEntry == null)
            {
                // If we don't already have a key, create one now
                if (null == (object)key)
                {
                    key = ObjectStateManager.CreateEntityKey(entitySet, wrappedEntity.Entity);
                }
                existingEntry = ObjectStateManager.FindEntityEntry(key);
            }

            if (null != existingEntry
                && !(doAttach && existingEntry.IsKeyEntry))
            {
                if (!ReferenceEquals(existingEntry.Entity, wrappedEntity.Entity))
                {
                    throw new InvalidOperationException(Strings.ObjectStateManager_ObjectStateManagerContainsThisEntityKey);
                }
                else
                {
                    var exptectedState = doAttach ? EntityState.Unchanged : EntityState.Added;

                    if (existingEntry.State != exptectedState)
                    {
                        throw doAttach
                                  ? new InvalidOperationException(Strings.ObjectContext_EntityAlreadyExistsInObjectStateManager)
                                  : new InvalidOperationException(
                                        Strings.ObjectStateManager_DoesnotAllowToReAddUnchangedOrModifiedOrDeletedEntity(
                                            existingEntry.State));
                    }
                    else
                    {
                        // AttachTo:
                        // Attach is no-op when the existing entry is not a KeyEntry
                        // and it's entity is the same entity instance and it's state is Unchanged

                        // AddObject:
                        // AddObject is no-op when the existing entry's entity is the same entity 
                        // instance and it's state is Added
                        isNoOperation = true;
                        return;
                    }
                }
            }
        }

        /// <summary>
        ///     Adds an object to the cache.  If it doesn't already have an entity key, the
        ///     entity set is determined based on the type and the O-C map.
        ///     If the object supports relationships (i.e. it implements IEntityWithRelationships),
        ///     this also sets the context onto its RelationshipManager object.
        /// </summary>
        /// <param name="entitySetName"> entitySetName the Object to be added. It might be qualifed with container name </param>
        /// <param name="entity"> Object to be added. </param>
        public virtual void AddObject(string entitySetName, object entity)
        {
            Check.NotNull(entity, "entity");

            Debug.Assert(!(entity is IEntityWrapper), "Object is an IEntityWrapper instance instead of the raw entity.");
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            EntityEntry existingEntry;
            var wrappedEntity = EntityWrapperFactory.WrapEntityUsingContextGettingEntry(entity, this, out existingEntry);

            if (existingEntry == null)
            {
                // If the exact object being added is already in the context, there there is no way we need to
                // load the type for it, and since this is expensive, we only do the load if we have to.

                // SQLBUDT 480919: Ensure the assembly containing the entity's CLR type is loaded into the workspace.
                // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
                // We will auto-load the entity type's assembly into the ObjectItemCollection.
                // We don't need the user's calling assembly for LoadAssemblyForType since entityType is sufficient.
                MetadataWorkspace.ImplicitLoadAssemblyForType(wrappedEntity.IdentityType, null);
            }
            else
            {
                Debug.Assert(
                    existingEntry.Entity == entity, "FindEntityEntry should return null if existing entry contains a different object.");
            }

            EntitySet entitySet;
            bool isNoOperation;

            VerifyRootForAdd(false, entitySetName, wrappedEntity, existingEntry, out entitySet, out isNoOperation);
            if (isNoOperation)
            {
                return;
            }

            var transManager = ObjectStateManager.TransactionManager;
            transManager.BeginAddTracking();

            try
            {
                var relationshipManager = wrappedEntity.RelationshipManager;
                Debug.Assert(relationshipManager != null, "Entity wrapper returned a null RelationshipManager");

                var doCleanup = true;
                try
                {
                    // Add the root of the graph to the cache.
                    AddSingleObject(entitySet, wrappedEntity, "entity");
                    doCleanup = false;
                }
                finally
                {
                    // If we failed after adding the entry but before completely attaching the related ends to the context, we need to do some cleanup.
                    // If the context is null, we didn't even get as far as trying to attach the RelationshipManager, so something failed before the entry
                    // was even added, therefore there is nothing to clean up.
                    if (doCleanup && wrappedEntity.Context == this)
                    {
                        // If the context is not null, it be because the failure happened after it was attached, or it
                        // could mean that this entity was already attached, in which case we don't want to clean it up
                        // If we find the entity in the context and its key is temporary, we must have just added it, so remove it now.
                        var entry = ObjectStateManager.FindEntityEntry(wrappedEntity.Entity);
                        if (entry != null
                            && entry.EntityKey.IsTemporary)
                        {
                            // devnote: relationshipManager is valid, so entity must be IEntityWithRelationships and casting is safe
                            relationshipManager.NodeVisited = true;
                            // devnote: even though we haven't added the rest of the graph yet, we need to go through the related ends and
                            //          clean them up, because some of them could have been attached to the context before the failure occurred
                            RelationshipManager.RemoveRelatedEntitiesFromObjectStateManager(wrappedEntity);
                            RelatedEnd.RemoveEntityFromObjectStateManager(wrappedEntity);
                        }
                        // else entry was not added or the key is not temporary, so it must have already been in the cache before we tried to add this product, so don't remove anything
                    }
                }

                relationshipManager.AddRelatedEntitiesToObjectStateManager( /*doAttach*/false);
            }
            finally
            {
                transManager.EndAddTracking();
                ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            }
        }

        /// <summary>
        ///     Adds an object to the cache without adding its related
        ///     entities.
        /// </summary>
        /// <param name="entity"> Object to be added. </param>
        /// <param name="setName"> EntitySet name for the Object to be added. It may be qualified with container name </param>
        /// <param name="containerName"> Container name for the Object to be added. </param>
        /// <param name="argumentName"> Name of the argument passed to a public method, for use in exceptions. </param>
        internal void AddSingleObject(EntitySet entitySet, IEntityWrapper wrappedEntity, string argumentName)
        {
            DebugCheck.NotNull(entitySet);
            DebugCheck.NotNull(wrappedEntity);
            DebugCheck.NotNull(wrappedEntity.Entity);

            var key = wrappedEntity.GetEntityKeyFromEntity();
            if (null != (object)key)
            {
                EntityUtil.ValidateEntitySetInKey(key, entitySet);
                key.ValidateEntityKey(_workspace, entitySet);
            }

            VerifyContextForAddOrAttach(wrappedEntity);
            wrappedEntity.Context = this;
            var entry = ObjectStateManager.AddEntry(wrappedEntity, null, entitySet, argumentName, true);

            // If the entity supports relationships, AttachContext on the
            // RelationshipManager object - with load option of
            // AppendOnly (if adding a new object to a context, set
            // the relationships up to cache by default -- load option
            // is only set to other values when AttachContext is
            // called by the materializer). Also add all related entitites to
            // cache.
            //
            // NOTE: AttachContext must be called after adding the object to
            // the cache--otherwise the object might not have a key
            // when the EntityCollections expect it to.            
            Debug.Assert(
                ObjectStateManager.TransactionManager.TrackProcessedEntities, "Expected tracking processed entities to be true when adding.");
            Debug.Assert(ObjectStateManager.TransactionManager.ProcessedEntities != null, "Expected non-null collection when flag set.");

            ObjectStateManager.TransactionManager.ProcessedEntities.Add(wrappedEntity);

            wrappedEntity.AttachContext(this, entitySet, MergeOption.AppendOnly);

            // Find PK values in referenced principals and use these to set FK values
            entry.FixupFKValuesFromNonAddedReferences();

            ObjectStateManager.FixupReferencesByForeignKeys(entry);
            wrappedEntity.TakeSnapshotOfRelationships(entry);
        }

        /// <summary>
        ///     Explicitly loads a referenced entity or collection of entities into the given entity.
        /// </summary>
        /// <remarks>
        ///     After loading, the referenced entity or collection can be accessed through the properties
        ///     of the source entity.
        /// </remarks>
        /// <param name="entity"> The source entity on which the relationship is defined </param>
        /// <param name="navigationProperty"> The name of the property to load </param>
        public virtual void LoadProperty(object entity, string navigationProperty)
        {
            var wrappedEntity = WrapEntityAndCheckContext(entity, "property");
            wrappedEntity.RelationshipManager.GetRelatedEnd(navigationProperty).Load();
        }

        /// <summary>
        ///     Explicitly loads a referenced entity or collection of entities into the given entity.
        /// </summary>
        /// <remarks>
        ///     After loading, the referenced entity or collection can be accessed through the properties
        ///     of the source entity.
        /// </remarks>
        /// <param name="entity"> The source entity on which the relationship is defined </param>
        /// <param name="navigationProperty"> The name of the property to load </param>
        /// <param name="mergeOption"> The merge option to use for the load </param>
        public virtual void LoadProperty(object entity, string navigationProperty, MergeOption mergeOption)
        {
            var wrappedEntity = WrapEntityAndCheckContext(entity, "property");
            wrappedEntity.RelationshipManager.GetRelatedEnd(navigationProperty).Load(mergeOption);
        }

        /// <summary>
        ///     Explicitly loads a referenced entity or collection of entities into the given entity.
        /// </summary>
        /// <remarks>
        ///     After loading, the referenced entity or collection can be accessed through the properties
        ///     of the source entity.
        ///     The property to load is specified by a LINQ expression which must be in the form of
        ///     a simple property member access.  For example, <code>(entity) => entity.PropertyName</code>
        ///     where PropertyName is the navigation property to be loaded.  Other expression forms will
        ///     be rejected at runtime.
        /// </remarks>
        /// <param name="entity"> The source entity on which the relationship is defined </param>
        /// <param name="selector"> A LINQ expression specifying the property to load </param>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public virtual void LoadProperty<TEntity>(TEntity entity, Expression<Func<TEntity, object>> selector)
        {
            // We used to throw an ArgumentException if the expression contained a Convert.  Now we remove the convert,
            // but if we still need to throw, then we should still throw an ArgumentException to avoid a breaking change.
            // Therefore, we keep track of whether or not we removed the convert.
            bool removedConvert;
            var navProp = ParsePropertySelectorExpression(selector, out removedConvert);
            var wrappedEntity = WrapEntityAndCheckContext(entity, "property");
            wrappedEntity.RelationshipManager.GetRelatedEnd(navProp, throwArgumentException: removedConvert).Load();
        }

        /// <summary>
        ///     Explicitly loads a referenced entity or collection of entities into the given entity.
        /// </summary>
        /// <remarks>
        ///     After loading, the referenced entity or collection can be accessed through the properties
        ///     of the source entity.
        ///     The property to load is specified by a LINQ expression which must be in the form of
        ///     a simple property member access.  For example, <code>(entity) => entity.PropertyName</code>
        ///     where PropertyName is the navigation property to be loaded.  Other expression forms will
        ///     be rejected at runtime.
        /// </remarks>
        /// <param name="entity"> The source entity on which the relationship is defined </param>
        /// <param name="selector"> A LINQ expression specifying the property to load </param>
        /// <param name="mergeOption"> The merge option to use for the load </param>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public virtual void LoadProperty<TEntity>(TEntity entity, Expression<Func<TEntity, object>> selector, MergeOption mergeOption)
        {
            // We used to throw an ArgumentException if the expression contained a Convert.  Now we remove the convert,
            // but if we still need to throw, then we should still throw an ArgumentException to avoid a breaking change.
            // Therefore, we keep track of whether or not we removed the convert.
            bool removedConvert;
            var navProp = ParsePropertySelectorExpression(selector, out removedConvert);
            var wrappedEntity = WrapEntityAndCheckContext(entity, "property");
            wrappedEntity.RelationshipManager.GetRelatedEnd(navProp, throwArgumentException: removedConvert).Load(mergeOption);
        }

        // Wraps the given entity and checks that it has a non-null context (i.e. that is is not detached).
        private IEntityWrapper WrapEntityAndCheckContext(object entity, string refType)
        {
            var wrappedEntity = EntityWrapperFactory.WrapEntityUsingContext(entity, this);
            if (wrappedEntity.Context == null)
            {
                throw new InvalidOperationException(Strings.ObjectContext_CannotExplicitlyLoadDetachedRelationships(refType));
            }

            if (wrappedEntity.Context
                != this)
            {
                throw new InvalidOperationException(Strings.ObjectContext_CannotLoadReferencesUsingDifferentContext(refType));
            }

            return wrappedEntity;
        }

        // Validates that the given property selector may represent a navigation property and returns the nav prop string.
        // The actual check that the navigation property is valid is performed by the
        // RelationshipManager while loading the RelatedEnd.
        internal static string ParsePropertySelectorExpression<TEntity>(Expression<Func<TEntity, object>> selector, out bool removedConvert)
        {
            Check.NotNull(selector, "selector");

            // We used to throw an ArgumentException if the expression contained a Convert.  Now we remove the convert,
            // but if we still need to throw, then we should still throw an ArgumentException to avoid a breaking change.
            // Therefore, we keep track of whether or not we removed the convert.
            removedConvert = false;
            var body = selector.Body;
            while (body.NodeType == ExpressionType.Convert
                   || body.NodeType == ExpressionType.ConvertChecked)
            {
                removedConvert = true;
                body = ((UnaryExpression)body).Operand;
            }

            var bodyAsMember = body as MemberExpression;
            if (bodyAsMember == null
                || !bodyAsMember.Member.DeclaringType.IsAssignableFrom(typeof(TEntity))
                || bodyAsMember.Expression.NodeType != ExpressionType.Parameter)
            {
                throw new ArgumentException(Strings.ObjectContext_SelectorExpressionMustBeMemberAccess);
            }

            return bodyAsMember.Member.Name;
        }

        /// <summary>
        ///     Apply modified properties to the original object.
        ///     This API is obsolete.  Please use ApplyCurrentValues instead.
        /// </summary>
        /// <param name="entitySetName"> name of EntitySet of entity to be updated </param>
        /// <param name="changed"> object with modified properties </param>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Browsable(false)]
        [Obsolete("Use ApplyCurrentValues instead")]
        public virtual void ApplyPropertyChanges(string entitySetName, object changed)
        {
            Check.NotNull(changed, "changed");
            Check.NotEmpty(entitySetName, "entitySetName");

            ApplyCurrentValues(entitySetName, changed);
        }

        /// <summary>
        ///     Apply modified properties to the original object.
        /// </summary>
        /// <param name="entitySetName"> name of EntitySet of entity to be updated </param>
        /// <param name="currentEntity"> object with modified properties </param>
        public virtual TEntity ApplyCurrentValues<TEntity>(string entitySetName, TEntity currentEntity) where TEntity : class
        {
            Check.NotNull(currentEntity, "currentEntity");
            Check.NotEmpty(entitySetName, "entitySetName");

            var wrappedEntity = EntityWrapperFactory.WrapEntityUsingContext(currentEntity, this);

            // SQLBUDT 480919: Ensure the assembly containing the entity's CLR type is loaded into the workspace.
            // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
            // We will auto-load the entity type's assembly into the ObjectItemCollection.
            // We don't need the user's calling assembly for LoadAssemblyForType since entityType is sufficient.
            MetadataWorkspace.ImplicitLoadAssemblyForType(wrappedEntity.IdentityType, null);

            var entitySet = GetEntitySetFromName(entitySetName);

            var key = wrappedEntity.EntityKey;
            if (null != (object)key)
            {
                EntityUtil.ValidateEntitySetInKey(key, entitySet, "entitySetName");
                key.ValidateEntityKey(_workspace, entitySet);
            }
            else
            {
                key = ObjectStateManager.CreateEntityKey(entitySet, currentEntity);
            }

            // Check if entity is already in the cache
            var entityEntry = ObjectStateManager.FindEntityEntry(key);
            if (entityEntry == null
                || entityEntry.IsKeyEntry)
            {
                throw new InvalidOperationException(Strings.ObjectStateManager_EntityNotTracked);
            }

            entityEntry.ApplyCurrentValuesInternal(wrappedEntity);

            return (TEntity)entityEntry.Entity;
        }

        /// <summary>
        ///     Apply original values to the entity.
        ///     The entity to update is found based on key values of the <paramref name="originalEntity" /> entity and the given
        ///     <paramref
        ///         name="entitySetName" />
        ///     .
        /// </summary>
        /// <param name="entitySetName"> Name of EntitySet of entity to be updated. </param>
        /// <param name="originalEntity"> Object with original values. </param>
        /// <returns> Updated entity. </returns>
        public virtual TEntity ApplyOriginalValues<TEntity>(string entitySetName, TEntity originalEntity) where TEntity : class
        {
            Check.NotNull(originalEntity, "originalEntity");

            Check.NotEmpty(entitySetName, "entitySetName");
            var wrappedOriginalEntity = EntityWrapperFactory.WrapEntityUsingContext(originalEntity, this);

            // SQLBUDT 480919: Ensure the assembly containing the entity's CLR type is loaded into the workspace.
            // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
            // We will auto-load the entity type's assembly into the ObjectItemCollection.
            // We don't need the user's calling assembly for LoadAssemblyForType since entityType is sufficient.
            MetadataWorkspace.ImplicitLoadAssemblyForType(wrappedOriginalEntity.IdentityType, null);

            var entitySet = GetEntitySetFromName(entitySetName);

            var key = wrappedOriginalEntity.EntityKey;
            if (null != (object)key)
            {
                EntityUtil.ValidateEntitySetInKey(key, entitySet, "entitySetName");
                key.ValidateEntityKey(_workspace, entitySet);
            }
            else
            {
                key = ObjectStateManager.CreateEntityKey(entitySet, originalEntity);
            }

            // Check if the entity is already in the cache
            var entityEntry = ObjectStateManager.FindEntityEntry(key);
            if (entityEntry == null
                || entityEntry.IsKeyEntry)
            {
                throw new InvalidOperationException(Strings.ObjectContext_EntityNotTrackedOrHasTempKey);
            }

            if (entityEntry.State != EntityState.Modified
                && entityEntry.State != EntityState.Unchanged
                && entityEntry.State != EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    Strings.ObjectContext_EntityMustBeUnchangedOrModifiedOrDeleted(entityEntry.State.ToString()));
            }

            if (entityEntry.WrappedEntity.IdentityType
                != wrappedOriginalEntity.IdentityType)
            {
                throw new ArgumentException(
                    Strings.ObjectContext_EntitiesHaveDifferentType(
                        entityEntry.Entity.GetType().FullName, originalEntity.GetType().FullName));
            }

            entityEntry.CompareKeyProperties(originalEntity);

            // The ObjectStateEntry.UpdateModifiedFields uses a variation of Shaper.UpdateRecord method 
            // which additionaly marks properties as modified as necessary.
            entityEntry.UpdateOriginalValues(wrappedOriginalEntity.Entity);

            // return the current entity
            return (TEntity)entityEntry.Entity;
        }

        /// <summary>
        ///     Attach entity graph into the context in the Unchanged state.
        ///     This version takes entity which doesn't have to have a Key.
        /// </summary>
        /// <param name="entitySetName"> EntitySet name for the Object to be attached. It may be qualified with container name. </param>
        /// <param name="entity"> The entity to be attached. </param>
        public virtual void AttachTo(string entitySetName, object entity)
        {
            Check.NotNull(entity, "entity");

            Debug.Assert(!(entity is IEntityWrapper), "Object is an IEntityWrapper instance instead of the raw entity.");
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();

            EntityEntry existingEntry;
            var wrappedEntity = EntityWrapperFactory.WrapEntityUsingContextGettingEntry(entity, this, out existingEntry);

            if (existingEntry == null)
            {
                // If the exact object being added is already in the context, there there is no way we need to
                // load the type for it, and since this is expensive, we only do the load if we have to.

                // SQLBUDT 480919: Ensure the assembly containing the entity's CLR type is loaded into the workspace.
                // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
                // We will auto-load the entity type's assembly into the ObjectItemCollection.
                // We don't need the user's calling assembly for LoadAssemblyForType since entityType is sufficient.
                MetadataWorkspace.ImplicitLoadAssemblyForType(wrappedEntity.IdentityType, null);
            }
            else
            {
                Debug.Assert(
                    existingEntry.Entity == entity, "FindEntityEntry should return null if existing entry contains a different object.");
            }

            EntitySet entitySet;
            bool isNoOperation;

            VerifyRootForAdd(true, entitySetName, wrappedEntity, existingEntry, out entitySet, out isNoOperation);
            if (isNoOperation)
            {
                return;
            }

            var transManager = ObjectStateManager.TransactionManager;
            transManager.BeginAttachTracking();

            try
            {
                ObjectStateManager.TransactionManager.OriginalMergeOption = wrappedEntity.MergeOption;
                var relationshipManager = wrappedEntity.RelationshipManager;
                Debug.Assert(relationshipManager != null, "Entity wrapper returned a null RelationshipManager");

                var doCleanup = true;
                try
                {
                    // Attach the root of entity graph to the cache.
                    AttachSingleObject(wrappedEntity, entitySet);
                    doCleanup = false;
                }
                finally
                {
                    // SQLBU 555615 Be sure that wrappedEntity.Context == this to not try to detach 
                    // entity from context if it was already attached to some other context.
                    // It's enough to check this only for the root of the graph since we can assume that all entities
                    // in the graph are attached to the same context (or none of them is attached).
                    if (doCleanup && wrappedEntity.Context == this)
                    {
                        // SQLBU 509900 RIConstraints: Entity still exists in cache after Attach fails
                        //
                        // Cleaning up is needed only when root of the graph violates some referential constraint.
                        // Normal cleaning is done in RelationshipManager.AddRelatedEntitiesToObjectStateManager()
                        // (referential constraints properties are checked in AttachSingleObject(), before
                        // AddRelatedEntitiesToObjectStateManager is called, that's why normal cleaning
                        // doesn't work in this case)

                        relationshipManager.NodeVisited = true;
                        // devnote: even though we haven't attached the rest of the graph yet, we need to go through the related ends and
                        //          clean them up, because some of them could have been attached to the context.
                        RelationshipManager.RemoveRelatedEntitiesFromObjectStateManager(wrappedEntity);
                        RelatedEnd.RemoveEntityFromObjectStateManager(wrappedEntity);
                    }
                }
                relationshipManager.AddRelatedEntitiesToObjectStateManager( /*doAttach*/true);
            }
            finally
            {
                transManager.EndAttachTracking();
                ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            }
        }

        /// <summary>
        ///     Attach entity graph into the context in the Unchanged state.
        ///     This version takes entity which does have to have a non-temporary Key.
        /// </summary>
        /// <param name="entity"> The entity to be attached. </param>
        public virtual void Attach(IEntityWithKey entity)
        {
            Check.NotNull(entity, "entity");

            if (null == (object)entity.EntityKey)
            {
                throw new InvalidOperationException(Strings.ObjectContext_CannotAttachEntityWithoutKey);
            }

            AttachTo(null, entity);
        }

        /// <summary>
        ///     Attaches single object to the cache without adding its related entities.
        /// </summary>
        /// <param name="entity"> Entity to be attached. </param>
        /// <param name="entitySet"> "Computed" entity set. </param>
        internal void AttachSingleObject(IEntityWrapper wrappedEntity, EntitySet entitySet)
        {
            DebugCheck.NotNull(wrappedEntity);
            DebugCheck.NotNull(wrappedEntity.Entity);
            DebugCheck.NotNull(entitySet);

            // Try to detect if the entity is invalid as soon as possible
            // (before adding the entity to the ObjectStateManager)
            var relationshipManager = wrappedEntity.RelationshipManager;
            Debug.Assert(relationshipManager != null, "Entity wrapper returned a null RelationshipManager");

            var key = wrappedEntity.GetEntityKeyFromEntity();
            if (null != (object)key)
            {
                EntityUtil.ValidateEntitySetInKey(key, entitySet);
                key.ValidateEntityKey(_workspace, entitySet);
            }
            else
            {
                key = ObjectStateManager.CreateEntityKey(entitySet, wrappedEntity.Entity);
            }

            Debug.Assert(key != null, "GetEntityKey should have returned a non-null key");

            // Temporary keys are not allowed
            if (key.IsTemporary)
            {
                throw new InvalidOperationException(Strings.ObjectContext_CannotAttachEntityWithTemporaryKey);
            }

            if (wrappedEntity.EntityKey != key)
            {
                wrappedEntity.EntityKey = key;
            }

            // Check if entity already exists in the cache.
            // NOTE: This check could be done earlier, but this way I avoid creating key twice.
            var entry = ObjectStateManager.FindEntityEntry(key);

            if (null != entry)
            {
                if (entry.IsKeyEntry)
                {
                    // devnote: SQLBU 555615. This method was extracted from PromoteKeyEntry to have consistent
                    // behavior of AttachTo in case of attaching entity which is already attached to some other context.
                    // We can not detect if entity is attached to another context until we call SetChangeTrackerOntoEntity
                    // which throws exception if the change tracker is already set.  
                    // SetChangeTrackerOntoEntity is now called from PromoteKeyEntryInitialization(). 
                    // Calling PromoteKeyEntryInitialization() before calling relationshipManager.AttachContext prevents
                    // overriding Context property on relationshipManager (and attaching relatedEnds to current context).
                    ObjectStateManager.PromoteKeyEntryInitialization(this, entry, wrappedEntity, replacingEntry: false);

                    Debug.Assert(
                        ObjectStateManager.TransactionManager.TrackProcessedEntities,
                        "Expected tracking processed entities to be true when adding.");
                    Debug.Assert(
                        ObjectStateManager.TransactionManager.ProcessedEntities != null, "Expected non-null collection when flag set.");

                    ObjectStateManager.TransactionManager.ProcessedEntities.Add(wrappedEntity);

                    wrappedEntity.TakeSnapshotOfRelationships(entry);

                    ObjectStateManager.PromoteKeyEntry(
                        entry,
                        wrappedEntity,
                        replacingEntry: false,
                        setIsLoaded: false,
                        keyEntryInitialized: true);

                    ObjectStateManager.FixupReferencesByForeignKeys(entry);

                    relationshipManager.CheckReferentialConstraintProperties(entry);
                }
                else
                {
                    Debug.Assert(!ReferenceEquals(entry.Entity, wrappedEntity.Entity));
                    throw new InvalidOperationException(Strings.ObjectStateManager_ObjectStateManagerContainsThisEntityKey);
                }
            }
            else
            {
                VerifyContextForAddOrAttach(wrappedEntity);
                wrappedEntity.Context = this;
                entry = ObjectStateManager.AttachEntry(key, wrappedEntity, entitySet);

                Debug.Assert(
                    ObjectStateManager.TransactionManager.TrackProcessedEntities,
                    "Expected tracking processed entities to be true when adding.");
                Debug.Assert(ObjectStateManager.TransactionManager.ProcessedEntities != null, "Expected non-null collection when flag set.");

                ObjectStateManager.TransactionManager.ProcessedEntities.Add(wrappedEntity);

                wrappedEntity.AttachContext(this, entitySet, MergeOption.AppendOnly);

                ObjectStateManager.FixupReferencesByForeignKeys(entry);
                wrappedEntity.TakeSnapshotOfRelationships(entry);

                relationshipManager.CheckReferentialConstraintProperties(entry);
            }
        }

        /// <summary>
        ///     When attaching we need to check that the entity is not already attached to a different context
        ///     before we wipe away that context.
        /// </summary>
        private void VerifyContextForAddOrAttach(IEntityWrapper wrappedEntity)
        {
            if (wrappedEntity.Context != null
                && wrappedEntity.Context != this
                && !wrappedEntity.Context.ObjectStateManager.IsDisposed
                && wrappedEntity.MergeOption != MergeOption.NoTracking)
            {
                throw new InvalidOperationException(Strings.Entity_EntityCantHaveMultipleChangeTrackers);
            }
        }

        /// <summary>
        ///     Create an entity key based on given entity set and values of given entity.
        /// </summary>
        /// <param name="entitySetName"> Entity set for the entity. </param>
        /// <param name="entity"> The entity. </param>
        /// <returns>
        ///     New instance of <see cref="EntityKey" /> for the provided <paramref name="entity" /> .
        /// </returns>
        public virtual EntityKey CreateEntityKey(string entitySetName, object entity)
        {
            Check.NotNull(entity, "entity");
            Debug.Assert(!(entity is IEntityWrapper), "Object is an IEntityWrapper instance instead of the raw entity.");
            Check.NotEmpty(entitySetName, "entitySetName");

            // SQLBUDT 480919: Ensure the assembly containing the entity's CLR type is loaded into the workspace.
            // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
            // We will auto-load the entity type's assembly into the ObjectItemCollection.
            // We don't need the user's calling assembly for LoadAssemblyForType since entityType is sufficient.
            MetadataWorkspace.ImplicitLoadAssemblyForType(EntityUtil.GetEntityIdentityType(entity.GetType()), null);

            var entitySet = GetEntitySetFromName(entitySetName);

            return ObjectStateManager.CreateEntityKey(entitySet, entity);
        }

        internal EntitySet GetEntitySetFromName(string entitySetName)
        {
            string setName;
            string containerName;

            GetEntitySetName(entitySetName, "entitySetName", this, out setName, out containerName);

            // Find entity set using entitySetName and entityContainerName
            return GetEntitySet(setName, containerName);
        }

        private void AddRefreshKey(
            object entityLike, Dictionary<EntityKey, EntityEntry> entities, Dictionary<EntitySet, List<EntityKey>> currentKeys)
        {
            Debug.Assert(!(entityLike is IEntityWrapper), "Object is an IEntityWrapper instance instead of the raw entity.");
            if (null == entityLike)
            {
                throw new InvalidOperationException(Strings.ObjectContext_NthElementIsNull(entities.Count));
            }

            var wrappedEntity = EntityWrapperFactory.WrapEntityUsingContext(entityLike, this);
            var key = wrappedEntity.EntityKey;
            RefreshCheck(entities, key);

            // Retrieve the EntitySet for the EntityKey and add an entry in the dictionary
            // that maps a set to the keys of entities that should be refreshed from that set.
            var entitySet = key.GetEntitySet(MetadataWorkspace);

            List<EntityKey> setKeys = null;
            if (!currentKeys.TryGetValue(entitySet, out setKeys))
            {
                setKeys = new List<EntityKey>();
                currentKeys.Add(entitySet, setKeys);
            }

            setKeys.Add(key);
        }

        /// <summary>
        ///     Creates an ObjectSet based on the EntitySet that is defined for TEntity.
        ///     Requires that the DefaultContainerName is set for the context and that there is a
        ///     single EntitySet for the specified type. Throws exception if more than one type is found.
        /// </summary>
        /// <typeparam name="TEntity"> Entity type for the requested ObjectSet </typeparam>
        public virtual ObjectSet<TEntity> CreateObjectSet<TEntity>()
            where TEntity : class
        {
            var entitySet = GetEntitySetForType(typeof(TEntity), "TEntity");
            return new ObjectSet<TEntity>(entitySet, this);
        }

        /// <summary>
        ///     Creates an ObjectSet based on the specified EntitySet name.
        /// </summary>
        /// <typeparam name="TEntity"> Expected type of the EntitySet </typeparam>
        /// <param name="entitySetName"> EntitySet to use for the ObjectSet. Can be fully-qualified or unqualified if the DefaultContainerName is set. </param>
        public virtual ObjectSet<TEntity> CreateObjectSet<TEntity>(string entitySetName)
            where TEntity : class
        {
            var entitySet = GetEntitySetForNameAndType(entitySetName, typeof(TEntity), "TEntity");
            return new ObjectSet<TEntity>(entitySet, this);
        }

        /// <summary>
        ///     Find the EntitySet in the default EntityContainer for the specified CLR type.
        ///     Must be a valid mapped entity type and must be mapped to exactly one EntitySet across all of the EntityContainers in the metadata for this context.
        /// </summary>
        /// <param name="entityCLRType"> CLR type to use for EntitySet lookup. </param>
        /// <returns> </returns>
        private EntitySet GetEntitySetForType(Type entityCLRType, string exceptionParameterName)
        {
            EntitySet entitySetForType = null;

            var defaultContainer = Perspective.GetDefaultContainer();
            if (defaultContainer == null)
            {
                // We don't have a default container, so look through all EntityContainers in metadata to see if
                // we can find exactly one EntitySet that matches the specified CLR type.
                var entityContainers = MetadataWorkspace.GetItems<EntityContainer>(DataSpace.CSpace);
                foreach (var entityContainer in entityContainers)
                {
                    // See if this container has exactly one EntitySet for this type
                    var entitySetFromContainer = GetEntitySetFromContainer(entityContainer, entityCLRType, exceptionParameterName);

                    if (entitySetFromContainer != null)
                    {
                        // Verify we haven't already found a matching EntitySet in some other container
                        if (entitySetForType != null)
                        {
                            // There is more than one EntitySet for this type across all containers in metadata, so we can't determine which one the user intended
                            throw new ArgumentException(
                                Strings.ObjectContext_MultipleEntitySetsFoundInAllContainers(entityCLRType.FullName), exceptionParameterName);
                        }

                        entitySetForType = entitySetFromContainer;
                    }
                }
            }
            else
            {
                // There is a default container, so restrict the search to EntitySets within it
                entitySetForType = GetEntitySetFromContainer(defaultContainer, entityCLRType, exceptionParameterName);
            }

            // We still may not have found a matching EntitySet for this type
            if (entitySetForType == null)
            {
                throw new ArgumentException(Strings.ObjectContext_NoEntitySetFoundForType(entityCLRType.FullName), exceptionParameterName);
            }

            return entitySetForType;
        }

        private EntitySet GetEntitySetFromContainer(EntityContainer container, Type entityCLRType, string exceptionParameterName)
        {
            // Verify that we have an EdmType mapping for the specified CLR type
            var entityEdmType = GetTypeUsage(entityCLRType).EdmType;

            // Try to find a single EntitySet for the specified type
            EntitySet entitySet = null;
            foreach (var es in container.BaseEntitySets)
            {
                // This is a match if the set is an EntitySet (not an AssociationSet) and the EntitySet
                // is defined for the specified entity type. Must be an exact match, not a base type. 
                if (es.BuiltInTypeKind == BuiltInTypeKind.EntitySet
                    && es.ElementType == entityEdmType)
                {
                    if (entitySet != null)
                    {
                        // There is more than one EntitySet for this type, so we can't determine which one the user intended
                        throw new ArgumentException(
                            Strings.ObjectContext_MultipleEntitySetsFoundInSingleContainer(entityCLRType.FullName, container.Name),
                            exceptionParameterName);
                    }

                    entitySet = (EntitySet)es;
                }
            }

            return entitySet;
        }

        /// <summary>
        ///     Finds an EntitySet with the specified name and verifies that its type matches the specified type.
        /// </summary>
        /// <param name="entitySetName"> Name of the EntitySet to find. Can be fully-qualified or unqualified if the DefaultContainerName is set </param>
        /// <param name="entityCLRType"> Expected CLR type of the EntitySet. Must exactly match the type for the EntitySet, base types are not valid. </param>
        /// <param name="exceptionParameterName"> Argument name to use if an exception occurs. </param>
        /// <returns> EntitySet that was found in metadata with the specified parameters </returns>
        private EntitySet GetEntitySetForNameAndType(string entitySetName, Type entityCLRType, string exceptionParameterName)
        {
            // Verify that the specified entitySetName exists in metadata
            var entitySet = GetEntitySetFromName(entitySetName);

            // Verify that the EntitySet type matches the specified type exactly (a base type is not valid)
            var entityEdmType = GetTypeUsage(entityCLRType).EdmType;
            if (entitySet.ElementType != entityEdmType)
            {
                throw new ArgumentException(
                    Strings.ObjectContext_InvalidObjectSetTypeForEntitySet(
                        entityCLRType.FullName, entitySet.ElementType.FullName, entitySetName), exceptionParameterName);
            }

            return entitySet;
        }

        #region Connection Management

        /// <summary>
        ///     Ensures that the connection is opened for an operation that requires an open connection to the store.
        ///     Calls to EnsureConnection MUST be matched with a single call to ReleaseConnection.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        ///     If the <see cref="ObjectContext" /> instance has been disposed.
        /// </exception>
        internal virtual void EnsureConnection()
        {
            if (Connection.State == ConnectionState.Broken)
            {
                Connection.Close();
            }

            if (Connection.State == ConnectionState.Closed)
            {
                Connection.Open();
                _openedConnection = true;
            }

            if (_openedConnection)
            {
                _connectionRequestCount++;
            }

            try
            {
                var currentTransaction = Transaction.Current;

                EnsureContextIsEnlistedInCurrentTransaction(
                    currentTransaction,
                    () =>
                        {
                            Connection.Open();
                            _openedConnection = true;
                            _connectionRequestCount++;
                            return true;
                        },
                    false);

                // If we get here, we have an open connection, either enlisted in the current
                // transaction (if it's non-null) or unenlisted from all transactions (if the
                // current transaction is null)
                _lastTransaction = currentTransaction;
            }
            catch (Exception)
            {
                // when the connection is unable to enlist properly or another error occured, be sure to release this connection
                ReleaseConnection();
                throw;
            }
        }

#if !NET40

        /// <summary>
        ///     Ensures that the connection is opened for an operation that requires an open connection to the store.
        ///     Calls to EnsureConnection MUST be matched with a single call to ReleaseConnection.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        ///     If the <see cref="ObjectContext" /> instance has been disposed.
        /// </exception>
        internal virtual async Task EnsureConnectionAsync(CancellationToken cancellationToken)
        {
            if (Connection.State == ConnectionState.Broken)
            {
                Connection.Close();
            }

            if (Connection.State == ConnectionState.Closed)
            {
                await Connection.OpenAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                _openedConnection = true;
            }

            if (_openedConnection)
            {
                _connectionRequestCount++;
            }

            try
            {
                var currentTransaction = Transaction.Current;

                await EnsureContextIsEnlistedInCurrentTransaction(
                    currentTransaction, async () =>
                                                  {
                                                      await
                                                          Connection.OpenAsync(cancellationToken).ConfigureAwait(
                                                              continueOnCapturedContext: false);
                                                      _openedConnection = true;
                                                      _connectionRequestCount++;
                                                      return true;
                                                  },
                    Task.FromResult(false)).ConfigureAwait(continueOnCapturedContext: false);

                // If we get here, we have an open connection, either enlisted in the current
                // transaction (if it's non-null) or unenlisted from all transactions (if the
                // current transaction is null)
                _lastTransaction = currentTransaction;
            }
            catch (Exception)
            {
                // when the connection is unable to enlist properly or another error occured, be sure to release this connection
                ReleaseConnection();
                throw;
            }
        }

#endif

        private T EnsureContextIsEnlistedInCurrentTransaction<T>(Transaction currentTransaction, Func<T> openConnection, T defaultValue)
        {
            // The following conditions are no longer valid since Metadata Independence.
            Debug.Assert(ConnectionState.Open == Connection.State, "Connection must be open.");

            // IF YOU MODIFIED THIS TABLE YOU MUST UPDATE TESTS IN SaveChangesTransactionTests SUITE ACCORDINGLY AS SOME CASES REFER TO NUMBERS IN THIS TABLE
            //
            // TABLE OF ACTIONS WE PERFORM HERE:
            //
            //  #  lastTransaction     currentTransaction         ConnectionState   WillClose      Action                                  Behavior when no explicit transaction (started with .ElistTransaction())     Behavior with explicit transaction (started with .ElistTransaction())
            //  1   null                null                       Open              No             no-op;                                  implicit transaction will be created and used                                explicit transaction should be used
            //  2   non-null tx1        non-null tx1               Open              No             no-op;                                  the last transaction will be used                                            N/A - it is not possible to EnlistTransaction if another transaction has already enlisted
            //  3   null                non-null                   Closed            Yes            connection.Open();                      Opening connection will automatically enlist into Transaction.Current        N/A - cannot enlist in transaction on a closed connection
            //  4   null                non-null                   Open              No             connection.Enlist(currentTransaction);  currentTransaction enlisted and used                                         N/A - it is not possible to EnlistTransaction if another transaction has already enlisted
            //  5   non-null            null                       Open              No             no-op;                                  implicit transaction will be created and used                                explicit transaction should be used
            //  6   non-null            null                       Closed            Yes            no-op;                                  implicit transaction will be created and used                                N/A - cannot enlist in transaction on a closed connection
            //  7   non-null tx1        non-null tx2               Open              No             connection.Enlist(currentTransaction);  currentTransaction enlisted and used                                         N/A - it is not possible to EnlistTransaction if another transaction has already enlisted
            //  8   non-null tx1        non-null tx2               Open              Yes            connection.Close(); connection.Open();  Re-opening connection will automatically enlist into Transaction.Current     N/A - only applies to TransactionScope - requires two transactions and CommitableTransaction and TransactionScope cannot be mixed
            //  9   non-null tx1        non-null tx2               Closed            Yes            connection.Open();                      Opening connection will automatcially enlist into Transaction.Current        N/A - cannot enlist in transaction on a closed connection

            var transactionHasChanged = (null != currentTransaction && !currentTransaction.Equals(_lastTransaction)) ||
                                        (null != _lastTransaction && !_lastTransaction.Equals(currentTransaction));

            if (transactionHasChanged)
            {
                if (!_openedConnection)
                {
                    // We didn't open the connection so, just try to enlist the connection in the current transaction. 
                    // Note that the connection can already be enlisted in a transaction (since the user opened 
                    // it s/he could enlist it manually using EntityConnection.EnlistTransaction() method). If the 
                    // transaction the connection is enlisted in has not completed (e.g. nested transaction) this call 
                    // will fail (throw). Also currentTransaction can be null here which means that the transaction
                    // used in the previous operation has completed. In this case we should not enlist the connection
                    // in "null" transaction as the user might have enlisted in a transaction manually between calls by 
                    // calling EntityConnection.EnlistTransaction() method. Enlisting with null would in this case mean "unenlist" 
                    // and would cause an exception (see above). Had the user not enlisted in a transaction between the calls
                    // enlisting with null would be a no-op - so again no reason to do it. 
                    if (currentTransaction != null)
                    {
                        Connection.EnlistTransaction(currentTransaction);
                    }
                }
                else if (_connectionRequestCount > 1)
                {
                    // We opened the connection. In addition we are here because there are multiple
                    // active requests going on (read: enumerators that has not been disposed yet) 
                    // using the same connection. (If there is only one active request e.g. like SaveChanges
                    // or single enumerator there is no need for any specific transaction handling - either
                    // we use the implicit ambient transaction (Transaction.Current) if one exists or we 
                    // will create our own local transaction. Also if there is only one active request
                    // the user could not enlist it in a transaction using EntityConnection.EnlistTransaction()
                    // because we opened the connection).
                    // If there are multiple active requests the user might have "played" with transactions
                    // after the first transaction. This code tries to deal with this kind of changes.

                    if (null == _lastTransaction)
                    {
                        Debug.Assert(currentTransaction != null, "transaction has changed and the lastTransaction was null");

                        // Two cases here: 
                        // - the previous operation was not run inside a transaction created by the user while this one is - just
                        //   enlist the connection in the transaction
                        // - the previous operation ran withing explicit transaction started with EntityConnection.EnlistTransaction()
                        //   method - try enlisting the connection in the transaction. This may fail however if the transactions 
                        //   are nested as you cannot enlist the connection in the transaction until the previous transaction has
                        //   completed.
                        Connection.EnlistTransaction(currentTransaction);
                    }
                    else
                    {
                        // We'll close and reopen the connection to get the benefit of automatic transaction enlistment.
                        // Remarks: We get here only if there is more than one active query (e.g. nested foreach or two subsequent queries or SaveChanges
                        // inside a for each) and each of these queries are using a different transaction (note that using TransactionScopeOption.Required 
                        // will not create a new transaction if an ambient transaction already exists - the ambient transaction will be used and we will 
                        // not end up in this code path). If we get here we are already in a loss-loss situation - we cannot enlist to the second transaction
                        // as this would cause an exception saying that there is already an active transaction that needs to be committed or rolled back
                        // before we can enlist the connection to a new transaction. The other option (and this is what we do here) is to close and reopen
                        // the connection. This will enlist the newly opened connection to the second transaction but will also close the reader being used
                        // by the first active query. As a result when trying to continue reading results from the first query the user will get an exception
                        // saying that calling "Read" on a closed data reader is not a valid operation.
                        Connection.Close();
                        return openConnection();
                    }
                }
            }
            else
            {
                // we don't need to do anything, nothing has changed.
            }

            return defaultValue;
        }

        /// <summary>
        ///     Resets the state of connection management when the connection becomes closed.
        /// </summary>
        /// <param name="sender"> </param>
        /// <param name="e"> </param>
        private void ConnectionStateChange(object sender, StateChangeEventArgs e)
        {
            if (e.CurrentState
                == ConnectionState.Closed)
            {
                _connectionRequestCount = 0;
                _openedConnection = false;
            }
        }

        /// <summary>
        ///     Releases the connection, potentially closing the connection if no active operations
        ///     require the connection to be open. There should be a single ReleaseConnection call
        ///     for each EnsureConnection call.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        ///     If the
        ///     <see cref="ObjectContext" />
        ///     instance has been disposed.
        /// </exception>
        internal virtual void ReleaseConnection()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(null, Strings.ObjectContext_ObjectDisposed);
            }

            if (_openedConnection)
            {
                Debug.Assert(_connectionRequestCount > 0, "_connectionRequestCount is zero or negative");
                if (_connectionRequestCount > 0)
                {
                    _connectionRequestCount--;
                }

                // When no operation is using the connection and the context had opened the connection
                // the connection can be closed
                if (_connectionRequestCount == 0)
                {
                    Connection.Close();
                    _openedConnection = false;
                }
            }
        }

        #endregion

        /// <summary>
        ///     Creates an ObjectQuery<typeparamref name="T" /> over the store, ready to be executed.
        /// </summary>
        /// <typeparam name="T"> Type of the query result </typeparam>
        /// <param name="queryString"> The query string to be executed </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns> An <see cref="ObjectQuery{T}"/> instance, ready to be executed </returns>
        public virtual ObjectQuery<T> CreateQuery<T>(string queryString, params ObjectParameter[] parameters)
        {
            Check.NotNull(queryString, "queryString");
            Check.NotNull(parameters, "parameters");

            // Ensure the assembly containing the entity's CLR type is loaded into the workspace.
            // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
            // We either auto-load <T>'s assembly into the ObjectItemCollection or we auto-load the user's calling assembly and its referenced assemblies.
            // If the entities in the user's result spans multiple assemblies, the user must manually call LoadFromAssembly.
            // *GetCallingAssembly returns the assembly of the method that invoked the currently executing method.
            MetadataWorkspace.ImplicitLoadAssemblyForType(typeof(T), Assembly.GetCallingAssembly());

            // create a ObjectQuery<T> with default settings
            var query = new ObjectQuery<T>(queryString, this, MergeOption.AppendOnly);

            foreach (var parameter in parameters)
            {
                query.Parameters.Add(parameter);
            }

            return query;
        }

        /// <summary>
        ///     Creates an EntityConnection from the given connection string.
        /// </summary>
        /// <param name="connectionString"> the connection string </param>
        /// <returns> the newly created connection </returns>
        [ResourceExposure(ResourceScope.Machine)] //Exposes the file names as part of ConnectionString which are a Machine resource
        [ResourceConsumption(ResourceScope.Machine)] //For EntityConnection constructor. But the paths are not created in this method.
        private static EntityConnection CreateEntityConnection(string connectionString)
        {
            Check.NotEmpty(connectionString, "connectionString");

            // create the connection
            var connection = new EntityConnection(connectionString);

            return connection;
        }

        /// <summary>
        ///     Given an entity connection, returns a copy of its MetadataWorkspace. Ensure we get
        ///     all of the metadata item collections by priming the entity connection.
        /// </summary>
        /// <returns> </returns>
        /// <exception cref="ObjectDisposedException">
        ///     If the
        ///     <see cref="ObjectContext" />
        ///     instance has been disposed.
        /// </exception>
        private MetadataWorkspace RetrieveMetadataWorkspaceFromConnection()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(null, Strings.ObjectContext_ObjectDisposed);
            }

            return _connection.GetMetadataWorkspace();
        }

        /// <summary>
        ///     Marks an object for deletion from the cache.
        /// </summary>
        /// <param name="entity"> Object to be deleted. </param>
        public virtual void DeleteObject(object entity)
        {
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            // This method and ObjectSet.DeleteObject are expected to have identical behavior except for the extra validation ObjectSet
            // requests by passing a non-null expectedEntitySetName. Any changes to this method are expected to be made in the common
            // internal overload below that ObjectSet also uses, unless there is a specific reason why a behavior is desired when the
            // call comes from ObjectContext only.
            DeleteObject(entity, null /*expectedEntitySetName*/);
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
        }

        /// <summary>
        ///     Common DeleteObject method that is used by both ObjectContext.DeleteObject and ObjectSet.DeleteObject.
        /// </summary>
        /// <param name="entity"> Object to be deleted. </param>
        /// <param name="expectedEntitySet"> EntitySet that the specified object is expected to be in. Null if the caller doesn't want to validate against a particular EntitySet. </param>
        internal void DeleteObject(object entity, EntitySet expectedEntitySet)
        {
            DebugCheck.NotNull(entity);
            Debug.Assert(!(entity is IEntityWrapper), "Object is an IEntityWrapper instance instead of the raw entity.");

            var cacheEntry = ObjectStateManager.FindEntityEntry(entity);
            if (cacheEntry == null
                || !ReferenceEquals(cacheEntry.Entity, entity))
            {
                throw new InvalidOperationException(Strings.ObjectContext_CannotDeleteEntityNotInObjectStateManager);
            }

            if (expectedEntitySet != null)
            {
                var actualEntitySet = cacheEntry.EntitySet;
                if (actualEntitySet != expectedEntitySet)
                {
                    throw new InvalidOperationException(
                        Strings.ObjectContext_EntityNotInObjectSet_Delete(
                            actualEntitySet.EntityContainer.Name, actualEntitySet.Name, expectedEntitySet.EntityContainer.Name,
                            expectedEntitySet.Name));
                }
            }

            cacheEntry.Delete();
            // Detaching from the context happens when the object
            // actually detaches from the cache (not just when it is
            // marked for deletion).
        }

        /// <summary>
        ///     Detach entity from the cache.
        /// </summary>
        /// <param name="entity"> Object to be detached. </param>
        public virtual void Detach(object entity)
        {
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();

            // This method and ObjectSet.DetachObject are expected to have identical behavior except for the extra validation ObjectSet
            // requests by passing a non-null expectedEntitySetName. Any changes to this method are expected to be made in the common
            // internal overload below that ObjectSet also uses, unless there is a specific reason why a behavior is desired when the
            // call comes from ObjectContext only.
            Detach(entity, expectedEntitySet: null);
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
        }

        /// <summary>
        ///     Common Detach method that is used by both ObjectContext.Detach and ObjectSet.Detach.
        /// </summary>
        /// <param name="entity"> Object to be detached. </param>
        /// <param name="expectedEntitySet"> EntitySet that the specified object is expected to be in. Null if the caller doesn't want to validate against a particular EntitySet. </param>
        internal void Detach(object entity, EntitySet expectedEntitySet)
        {
            DebugCheck.NotNull(entity);
            Debug.Assert(!(entity is IEntityWrapper), "Object is an IEntityWrapper instance instead of the raw entity.");

            var cacheEntry = ObjectStateManager.FindEntityEntry(entity);

            // this condition includes key entries and relationship entries
            if (cacheEntry == null
                || !ReferenceEquals(cacheEntry.Entity, entity)
                || cacheEntry.Entity == null)
            {
                throw new InvalidOperationException(Strings.ObjectContext_CannotDetachEntityNotInObjectStateManager);
            }

            if (expectedEntitySet != null)
            {
                var actualEntitySet = cacheEntry.EntitySet;
                if (actualEntitySet != expectedEntitySet)
                {
                    throw new InvalidOperationException(
                        Strings.ObjectContext_EntityNotInObjectSet_Detach(
                            actualEntitySet.EntityContainer.Name, actualEntitySet.Name, expectedEntitySet.EntityContainer.Name,
                            expectedEntitySet.Name));
                }
            }

            cacheEntry.Detach();
        }

        /// <summary>
        ///     Disposes this ObjectContext.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1063:ImplementIDisposableCorrectly")]
        public void Dispose()
        {
            // Technically, calling GC.SuppressFinalize is not required because the class does not
            // have a finalizer, but it does no harm, protects against the case where a finalizer is added
            // in the future, and prevents an FxCop warning.
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Disposes this ObjectContext.
        /// </summary>
        /// <param name="disposing"> true to release both managed and unmanaged resources; false to release only unmanaged resources. </param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Release managed resources here.
                    if (_connection != null)
                    {
                        _connection.StateChange -= ConnectionStateChange;

                        // Dispose the connection the ObjectContext created
                        if (_createdConnection)
                        {
                            _connection.Dispose();
                        }
                    }
                    _connection = null; // Marks this object as disposed.
                    _adapter = null;
                    if (_objectStateManager != null)
                    {
                        _objectStateManager.Dispose();
                    }
                }

                _disposed = true;
            }
        }

        #region GetEntitySet

        /// <summary>
        ///     Returns the EntitySet with the given name from given container.
        /// </summary>
        /// <param name="entitySetName"> Name of entity set. </param>
        /// <param name="entityContainerName"> Name of container. </param>
        /// <returns> The appropriate EntitySet. </returns>
        /// <exception cref="InvalidOperationException">The entity set could not be found for the given name.</exception>
        /// <exception cref="InvalidOperationException">The entity container could not be found for the given name.</exception>
        internal EntitySet GetEntitySet(string entitySetName, string entityContainerName)
        {
            DebugCheck.NotNull(entitySetName);

            EntityContainer container = null;

            if (String.IsNullOrEmpty(entityContainerName))
            {
                container = Perspective.GetDefaultContainer();
                Debug.Assert(container != null, "Problem with metadata - default container not found");
            }
            else
            {
                if (!MetadataWorkspace.TryGetEntityContainer(entityContainerName, DataSpace.CSpace, out container))
                {
                    throw new InvalidOperationException(Strings.ObjectContext_EntityContainerNotFoundForName(entityContainerName));
                }
            }

            EntitySet entitySet = null;

            if (!container.TryGetEntitySetByName(entitySetName, false, out entitySet))
            {
                throw new InvalidOperationException(
                    Strings.ObjectContext_EntitySetNotFoundForName(TypeHelpers.GetFullName(container.Name, entitySetName)));
            }

            return entitySet;
        }

        private static void GetEntitySetName(
            string qualifiedName, string parameterName, ObjectContext context, out string entityset, out string container)
        {
            entityset = null;
            container = null;
            Check.NotEmpty(qualifiedName, parameterName);

            var result = qualifiedName.Split('.');
            if (result.Length > 2)
            {
                throw new ArgumentException(Strings.ObjectContext_QualfiedEntitySetName, parameterName);
            }
            if (result.Length == 1) // if not '.' at all
            {
                entityset = result[0];
            }
            else
            {
                container = result[0];
                entityset = result[1];
                if (container == null
                    || container.Length == 0) // if it starts with '.'
                {
                    throw new ArgumentException(Strings.ObjectContext_QualfiedEntitySetName, parameterName);
                }
            }
            if (entityset == null
                || entityset.Length == 0) // if it's not in the form "ES name . containername"
            {
                throw new ArgumentException(Strings.ObjectContext_QualfiedEntitySetName, parameterName);
            }

            if (context != null
                && String.IsNullOrEmpty(container)
                && context.Perspective.GetDefaultContainer() == null)
            {
                throw new ArgumentException(Strings.ObjectContext_ContainerQualifiedEntitySetNameRequired, parameterName);
            }
        }

        /// <summary>
        ///     Validate that an EntitySet is compatible with a given entity instance's CLR type.
        /// </summary>
        /// <param name="entitySet"> an EntitySet </param>
        /// <param name="entityType"> The CLR type of an entity instance </param>
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly")]
        private void ValidateEntitySet(EntitySet entitySet, Type entityType)
        {
            var entityTypeUsage = GetTypeUsage(entityType);
            if (!entitySet.ElementType.IsAssignableFrom(entityTypeUsage.EdmType))
            {
                throw new ArgumentException(Strings.ObjectContext_InvalidEntitySetOnEntity(entitySet.Name, entityType), "entity");
            }
        }

        internal TypeUsage GetTypeUsage(Type entityCLRType)
        {
            // Register the assembly so the type information will be sure to be loaded in metadata
            MetadataWorkspace.ImplicitLoadAssemblyForType(entityCLRType, Assembly.GetCallingAssembly());

            TypeUsage entityTypeUsage = null;
            if (!Perspective.TryGetType(entityCLRType, out entityTypeUsage)
                || !TypeSemantics.IsEntityType(entityTypeUsage))
            {
                Debug.Assert(entityCLRType != null, "The type cannot be null.");
                throw new InvalidOperationException(Strings.ObjectContext_NoMappingForEntityType(entityCLRType.FullName));
            }

            Debug.Assert(entityTypeUsage != null, "entityTypeUsage is null");
            return entityTypeUsage;
        }

        #endregion

        /// <summary>
        ///     Retrieves an object from the cache if present or from the
        ///     store if not.
        /// </summary>
        /// <param name="key"> Key of the object to be found. </param>
        /// <returns> Entity object. </returns>
        public virtual object GetObjectByKey(EntityKey key)
        {
            Check.NotNull(key, "key");

            var entitySet = key.GetEntitySet(MetadataWorkspace);
            Debug.Assert(entitySet != null, "Key's EntitySet should not be null in the MetadataWorkspace");

            // Ensure the assembly containing the entity's CLR type is loaded into the workspace.
            // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
            // Either the entity type's assembly is already in the ObjectItemCollection or we auto-load the user's calling assembly and its referenced assemblies.
            // *GetCallingAssembly returns the assembly of the method that invoked the currently executing method.
            MetadataWorkspace.ImplicitLoadFromEntityType(entitySet.ElementType, Assembly.GetCallingAssembly());

            object entity;
            if (!TryGetObjectByKey(key, out entity))
            {
                throw new ObjectNotFoundException(Strings.ObjectContext_ObjectNotFound);
            }

            return entity;
        }

        #region Refresh

        /// <summary>
        ///     Refreshing cache data with store data for specific entities.
        ///     The order in which entites are refreshed is non-deterministic.
        /// </summary>
        /// <param name="refreshMode"> Determines how the entity retrieved from the store is merged with the entity in the cache </param>
        /// <param name="collection"> must not be null and all entities must be attached to this context. May be empty. </param>
        /// <exception cref="ArgumentOutOfRangeException">if refreshMode is not valid</exception>
        /// <exception cref="ArgumentNullException">collection is null</exception>
        /// <exception cref="ArgumentException">collection contains null or non entities or entities not attached to this context</exception>
        public virtual void Refresh(RefreshMode refreshMode, IEnumerable collection)
        {
            Check.NotNull(collection, "collection");

            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            try
            {
                EntityUtil.CheckArgumentRefreshMode(refreshMode);
                // collection may not contain any entities -- this is valid for this overload
                RefreshEntities(refreshMode, collection);
            }
            finally
            {
                ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            }
        }

        /// <summary>
        ///     Refreshing cache data with store data for a specific entity.
        /// </summary>
        /// <param name="refreshMode"> Determines how the entity retrieved from the store is merged with the entity in the cache </param>
        /// <param name="entity"> The entity to refresh. This must be a non-null entity that is attached to this context </param>
        /// <exception cref="ArgumentOutOfRangeException">if refreshMode is not valid</exception>
        /// <exception cref="ArgumentNullException">entity is null</exception>
        /// <exception cref="ArgumentException">entity is not attached to this context</exception>
        public virtual void Refresh(RefreshMode refreshMode, object entity)
        {
            Check.NotNull(entity, "entity");
            Debug.Assert(!(entity is IEntityWrapper), "Object is an IEntityWrapper instance instead of the raw entity.");

            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            try
            {
                EntityUtil.CheckArgumentRefreshMode(refreshMode);
                RefreshEntities(refreshMode, new[] { entity });
            }
            finally
            {
                ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            }
        }

        /// <summary>
        ///     Validates that the given entity/key pair has an ObjectStateEntry
        ///     and that entry is not in the added state.
        ///     The entity is added to the entities dictionary, and checked for duplicates.
        /// </summary>
        /// <param name="entities"> on exit, entity is added to this dictionary. </param>
        /// <param name="key"> </param>
        private void RefreshCheck(
            Dictionary<EntityKey, EntityEntry> entities, EntityKey key)
        {
            var entry = ObjectStateManager.FindEntityEntry(key);
            if (null == entry)
            {
                throw new InvalidOperationException(Strings.ObjectContext_NthElementNotInObjectStateManager(entities.Count));
            }

            if (EntityState.Added
                == entry.State)
            {
                throw new InvalidOperationException(Strings.ObjectContext_NthElementInAddedState(entities.Count));
            }

            Debug.Assert(EntityState.Added != entry.State, "not expecting added");
            Debug.Assert(EntityState.Detached != entry.State, "not expecting detached");

            try
            {
                entities.Add(key, entry); // don't ignore duplicates
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(Strings.ObjectContext_NthElementIsDuplicate(entities.Count));
            }

            Debug.Assert(null != (object)key, "null entity.Key");
            Debug.Assert(null != key.EntitySetName, "null entity.Key.EntitySetName");
        }

        private void RefreshEntities(RefreshMode refreshMode, IEnumerable collection)
        {
            // refreshMode and collection should already be validated prior to this call -- collection can be empty in one Refresh overload
            // but not in the other, so we need to do that check before we get to this common method
            DebugCheck.NotNull(collection);

            var openedConnection = false;

            try
            {
                var entities = new Dictionary<EntityKey, EntityEntry>(RefreshEntitiesSize(collection));

                #region 1) Validate and bucket the entities by entity set

                var refreshKeys = new Dictionary<EntitySet, List<EntityKey>>();
                foreach (var entity in collection) // anything other than object risks InvalidCastException
                {
                    AddRefreshKey(entity, entities, refreshKeys);
                }

                // The collection is no longer required at this point.
                collection = null;

                #endregion

                #region 2) build and execute the query for each set of entities

                if (refreshKeys.Count > 0)
                {
                    EnsureConnection();
                    openedConnection = true;

                    // All entities from a single set can potentially be refreshed in the same query.
                    // However, the refresh operations are batched in an attempt to avoid the generation
                    // of query trees or provider SQL that exhaust available client or server resources.
                    foreach (var targetSet in refreshKeys.Keys)
                    {
                        var setKeys = refreshKeys[targetSet];
                        var refreshedCount = 0;
                        while (refreshedCount < setKeys.Count)
                        {
                            refreshedCount = BatchRefreshEntitiesByKey(refreshMode, entities, targetSet, setKeys, refreshedCount);
                        }
                    }
                }

                // The refreshKeys list is no longer required at this point.
                refreshKeys = null;

                #endregion

                #region 3) process the unrefreshed entities

                if (RefreshMode.StoreWins == refreshMode)
                {
                    // remove all entites that have been removed from the store, not added by client
                    foreach (var item in entities)
                    {
                        Debug.Assert(EntityState.Added != item.Value.State, "should not be possible");
                        if (EntityState.Detached
                            != item.Value.State)
                        {
                            // We set the detaching flag here even though we are deleting because we are doing a
                            // Delete/AcceptChanges cycle to simulate a Detach, but we can't use Detach directly
                            // because legacy behavior around cascade deletes should be preserved.  However, we
                            // do want to prevent FK values in dependents from being nulled, which is why we
                            // need to set the detaching flag.
                            ObjectStateManager.TransactionManager.BeginDetaching();
                            try
                            {
                                item.Value.Delete();
                            }
                            finally
                            {
                                ObjectStateManager.TransactionManager.EndDetaching();
                            }
                            Debug.Assert(EntityState.Detached != item.Value.State, "not expecting detached");

                            item.Value.AcceptChanges();
                        }
                    }
                }
                else if (RefreshMode.ClientWins == refreshMode
                         && 0 < entities.Count)
                {
                    // throw an exception with all appropriate entity keys in text
                    var prefix = String.Empty;
                    var builder = new StringBuilder();
                    foreach (var item in entities)
                    {
                        Debug.Assert(EntityState.Added != item.Value.State, "should not be possible");
                        if (item.Value.State
                            == EntityState.Deleted)
                        {
                            // Detach the deleted items because this is the client changes and the server
                            // does not have these items any more
                            item.Value.AcceptChanges();
                        }
                        else
                        {
                            builder.Append(prefix).Append(Environment.NewLine);
                            builder.Append('\'').Append(item.Key.ConcatKeyValue()).Append('\'');
                            prefix = ",";
                        }
                    }

                    // If there were items that could not be found, throw an exception
                    if (builder.Length > 0)
                    {
                        throw new InvalidOperationException(Strings.ObjectContext_ClientEntityRemovedFromStore(builder.ToString()));
                    }
                }

                #endregion
            }
            finally
            {
                if (openedConnection)
                {
                    ReleaseConnection();
                }
            }
        }

        private int BatchRefreshEntitiesByKey(
            RefreshMode refreshMode, Dictionary<EntityKey, EntityEntry> trackedEntities,
            EntitySet targetSet, List<EntityKey> targetKeys, int startFrom)
        {
            var queryTreeAndNextPosition = PrepareRefreshQuery(targetSet, targetKeys, startFrom);

            // Evaluate the refresh query using ObjectQuery<T> and process the results to update the ObjectStateManager.
            var mergeOption = (RefreshMode.StoreWins == refreshMode
                                   ? MergeOption.OverwriteChanges
                                   : MergeOption.PreserveChanges);
            var objectQueryExecutionPlan = _objectQueryExecutionPlanFactory.Prepare(
                this, queryTreeAndNextPosition.Item1, typeof(object), mergeOption,
                /*streaming:*/ false, null, null, DbExpressionBuilder.AliasGenerator);

            var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);
            var results = executionStrategy.Execute(
                () => ExecuteInTransaction(
                    () => objectQueryExecutionPlan.Execute<object>(this, null),
                    throwOnExistingTransaction: executionStrategy.RetriesOnFailure, startLocalTransaction: false,
                    releaseConnectionOnSuccess: true));

            foreach (var entity in results)
            {
                // There is a risk that, during an event, the Entity removed itself from the cache.
                var entry = ObjectStateManager.FindEntityEntry(entity);
                if (null != entry
                    && EntityState.Modified == entry.State)
                {
                    // this is 'ForceChanges' - which is the same as PreserveChanges, except all properties are marked modified.
                    Debug.Assert(RefreshMode.ClientWins == refreshMode, "StoreWins always becomes unchanged");
                    entry.SetModifiedAll();
                }

                var wrappedEntity = EntityWrapperFactory.WrapEntityUsingContext(entity, this);
                var key = wrappedEntity.EntityKey;
                if ((object)key == null)
                {
                    throw Error.EntityKey_UnexpectedNull();
                }

                // An incorrectly returned entity should result in an exception to avoid further corruption to the ObjectStateManager.
                if (!trackedEntities.Remove(key))
                {
                    throw new InvalidOperationException(Strings.ObjectContext_StoreEntityNotPresentInClient);
                }
            }

            // Return the position in the list from which the next refresh operation should start.
            // This will be equal to the list count if all remaining entities in the list were
            // refreshed during this call.
            return queryTreeAndNextPosition.Item2;
        }

        internal virtual Tuple<DbQueryCommandTree, int> PrepareRefreshQuery(EntitySet targetSet, List<EntityKey> targetKeys, int startFrom)
        {
            // A single refresh query can be built for all entities from the same set.
            // For each entity set, a DbFilterExpression is constructed that
            // expresses the equivalent of:
            //
            // SELECT VALUE e
            // FROM <entityset> AS e
            // WHERE
            // GetRefKey(GetEntityRef(e)) == <ref1>.KeyValues
            // [OR GetRefKey(GetEntityRef(e)) == <ref2>.KeyValues
            // [..OR GetRefKey(GetEntityRef(e)) == <refN>.KeyValues]]
            //
            // Note that a LambdaFunctionExpression is used so that instead
            // of repeating GetRefKey(GetEntityRef(e)) a VariableReferenceExpression
            // to a Lambda argument with the value GetRefKey(GetEntityRef(e)) is used instead.
            // The query is therefore logically equivalent to:
            //
            // SELECT VALUE e
            // FROM <entityset> AS e
            // WHERE
            //   LET(x = GetRefKey(GetEntityRef(e)) IN (
            //      x == <ref1>.KeyValues
            //     [OR x == <ref2>.KeyValues
            //     [..OR x == <refN>.KeyValues]]
            //   )

            // The batch size determines the maximum depth of the predicate OR tree and
            // also limits the size of the generated provider SQL that is sent to the server.
            const int maxBatch = 250;

            // Bind the target EntitySet under the name "EntitySet".
            var entitySetBinding = targetSet.Scan().BindAs("EntitySet");

            // Use the variable from the set binding as the 'e' in a new GetRefKey(GetEntityRef(e)) expression.
            DbExpression sourceEntityKey = entitySetBinding.Variable.GetEntityRef().GetRefKey();

            // Build the where predicate as described above. A maximum of <batchsize> entity keys will be included
            // in the predicate, starting from position <startFrom> in the list of entity keys. As each key is
            // included, both <batchsize> and <startFrom> are incremented to ensure that the batch size is
            // correctly constrained and that the new starting position for the next call to this method is calculated.
            var batchSize = Math.Min(maxBatch, (targetKeys.Count - startFrom));
            var keyFilters = new DbExpression[batchSize];
            for (var idx = 0; idx < batchSize; idx++)
            {
                // Create a row constructor expression based on the key values of the EntityKey.
                var keyValueColumns = targetKeys[startFrom++].GetKeyValueExpressions(targetSet);
                DbExpression keyFilter = DbExpressionBuilder.NewRow(keyValueColumns);

                // Create an equality comparison between the row constructor and the lambda variable
                // that refers to GetRefKey(GetEntityRef(e)), which also produces a row
                // containing key values, but for the current entity from the entity set.
                keyFilters[idx] = sourceEntityKey.Equal(keyFilter);
            }

            // Sanity check that the batch includes at least one element.
            Debug.Assert(batchSize > 0, "Didn't create a refresh expression?");

            // Build a balanced binary tree that OR's the key filters together.
            var entitySetFilter = Helpers.BuildBalancedTreeInPlace(keyFilters, DbExpressionBuilder.Or);

            // Create a FilterExpression based on the EntitySet binding and the Lambda predicate.
            // This FilterExpression encapsulated the logic required for the refresh query as described above.
            DbExpression refreshQuery = entitySetBinding.Filter(entitySetFilter);

            // Initialize the command tree used to issue the refresh query.
            return new Tuple<DbQueryCommandTree, int>(
                DbQueryCommandTree.FromValidExpression(MetadataWorkspace, DataSpace.CSpace, refreshQuery),
                startFrom);
        }

        private static int RefreshEntitiesSize(IEnumerable collection)
        {
            var list = collection as ICollection;
            return null != list ? list.Count : 0;
        }

        #endregion

        #region SaveChanges

        /// <summary>
        ///     Persists all updates to the store.
        /// </summary>
        /// <returns> The number of dirty (i.e., Added, Modified, or Deleted) ObjectStateEntries in the ObjectStateManager when SaveChanges was called. </returns>
        public int SaveChanges()
        {
            return SaveChanges(SaveOptions.DetectChangesBeforeSave | SaveOptions.AcceptAllChangesAfterSave);
        }

#if !NET40

        /// <summary>
        ///     An asynchronous version of SaveChanges, which
        ///     persists all updates to the store.
        /// </summary>
        /// <returns> A task representing the asynchronous operation </returns>
        public Task<Int32> SaveChangesAsync()
        {
            return SaveChangesAsync(SaveOptions.DetectChangesBeforeSave | SaveOptions.AcceptAllChangesAfterSave, CancellationToken.None);
        }

        /// <summary>
        ///     An asynchronous version of SaveChanges, which
        ///     persists all updates to the store.
        /// </summary>
        /// <param name="cancellationToken"> The token to monitor for cancellation requests </param>
        /// <returns> A task representing the asynchronous operation </returns>
        public Task<Int32> SaveChangesAsync(CancellationToken cancellationToken)
        {
            return SaveChangesAsync(SaveOptions.DetectChangesBeforeSave | SaveOptions.AcceptAllChangesAfterSave, cancellationToken);
        }

#endif

        /// <summary>
        ///     Persists all updates to the store.
        ///     This API is obsolete.  Please use SaveChanges(SaveOptions options) instead.
        ///     SaveChanges(true) is equivalent to SaveChanges() -- That is it detects changes and
        ///     accepts all changes after save.
        ///     SaveChanges(false) detects changes but does not accept changes after save.
        /// </summary>
        /// <param name="acceptChangesDuringSave"> if false, user must call AcceptAllChanges </param>
        /// />
        /// <returns> The number of dirty (i.e., Added, Modified, or Deleted) ObjectStateEntries in the ObjectStateManager when SaveChanges was called. </returns>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Browsable(false)]
        [Obsolete("Use SaveChanges(SaveOptions options) instead.")]
        public int SaveChanges(bool acceptChangesDuringSave)
        {
            return SaveChanges(
                acceptChangesDuringSave
                    ? SaveOptions.DetectChangesBeforeSave | SaveOptions.AcceptAllChangesAfterSave
                    : SaveOptions.DetectChangesBeforeSave);
        }

        /// <summary>
        ///     Persists all updates to the store.
        /// </summary>
        /// <param name="options"> Describes behavior options of SaveChanges </param>
        /// <returns> The number of dirty (i.e., Added, Modified, or Deleted) ObjectStateEntries in the ObjectStateManager processed by SaveChanges. </returns>
        public virtual int SaveChanges(SaveOptions options)
        {
            PrepareToSaveChanges(options);

            var entriesAffected =
                ObjectStateManager.GetObjectStateEntriesCount(EntityState.Added | EntityState.Deleted | EntityState.Modified);

            // if there are no changes to save, perform fast exit to avoid interacting with or starting of new transactions
            if (0 < entriesAffected)
            {
                var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);
                entriesAffected = executionStrategy.Execute(
                    () => SaveChangesToStore(
                        options, throwOnExistingTransaction: executionStrategy.RetriesOnFailure));
            }

            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            return entriesAffected;
        }

#if !NET40

        /// <summary>
        ///     An asynchronous version of SaveChanges, which
        ///     persists all updates to the store.
        /// </summary>
        /// <param name="options"> Describes behavior options of SaveChanges </param>
        /// <returns> A task representing the asynchronous operation </returns>
        public Task<Int32> SaveChangesAsync(SaveOptions options)
        {
            return SaveChangesAsync(options, CancellationToken.None);
        }

        /// <summary>
        ///     An asynchronous version of SaveChanges, which persists all updates to the store.
        /// </summary>
        /// <param name="options"> Describes behavior options of SaveChanges </param>
        /// <param name="cancellationToken"> The token to monitor for cancellation requests </param>
        /// <returns> A task representing the asynchronous operation </returns>
        public virtual async Task<Int32> SaveChangesAsync(SaveOptions options, CancellationToken cancellationToken)
        {
            PrepareToSaveChanges(options);

            var entriesAffected =
                ObjectStateManager.GetObjectStateEntriesCount(EntityState.Added | EntityState.Deleted | EntityState.Modified);

            // if there are no changes to save, perform fast exit to avoid interacting with or starting of new transactions
            if (0 < entriesAffected)
            {
                var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);
                entriesAffected = await executionStrategy.ExecuteAsync(
                    () => SaveChangesToStoreAsync(
                        options,
                              /*throwOnExistingTransaction:*/ executionStrategy.RetriesOnFailure, cancellationToken))
                                                         .ConfigureAwait(continueOnCapturedContext: false);
            }

            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            return entriesAffected;
        }

#endif

        private void PrepareToSaveChanges(SaveOptions options)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(null, Strings.ObjectContext_ObjectDisposed);
            }

            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();

            OnSavingChanges();

            if ((SaveOptions.DetectChangesBeforeSave & options) != 0)
            {
                ObjectStateManager.DetectChanges();
            }

            if (ObjectStateManager.SomeEntryWithConceptualNullExists())
            {
                throw new InvalidOperationException(Strings.ObjectContext_CommitWithConceptualNull);
            }
        }

        private int SaveChangesToStore(SaveOptions options, bool throwOnExistingTransaction)
        {
            // get data adapter
            if (_adapter == null)
            {
                _adapter = (IEntityAdapter)((IServiceProvider)EntityProviderFactory.Instance).GetService(typeof(IEntityAdapter));
            }

            // only accept changes after the local transaction commits
            _adapter.AcceptChangesDuringUpdate = false;
            _adapter.Connection = Connection;
            _adapter.CommandTimeout = CommandTimeout;

            int entriesAffected;
            if (_commandInterceptor == null
                || !_commandInterceptor.IsEnabled)
            {
                entriesAffected = ExecuteInTransaction(
                    () => _adapter.Update(ObjectStateManager, throwOnClosedConnection: true), throwOnExistingTransaction,
                    startLocalTransaction: true, releaseConnectionOnSuccess: true);
            }
            else
            {
                entriesAffected = _adapter.Update(ObjectStateManager, false);
            }

            if ((SaveOptions.AcceptAllChangesAfterSave & options) != 0)
            {
                try
                {
                    AcceptAllChanges();
                }
                catch (Exception e)
                {
                    // If AcceptAllChanges throws - let's inform user that changes in database were committed 
                    // and that Context and Database can be in inconsistent state.
                    throw new InvalidOperationException(Strings.ObjectContext_AcceptAllChangesFailure(e.Message));
                }
            }

            return entriesAffected;
        }

#if !NET40

        private async Task<int> SaveChangesToStoreAsync(
            SaveOptions options, bool throwOnExistingTransaction, CancellationToken cancellationToken)
        {
            if (_adapter == null)
            {
                _adapter = (IEntityAdapter)((IServiceProvider)EntityProviderFactory.Instance).GetService(typeof(IEntityAdapter));
            }

            // only accept changes after the local transaction commits
            _adapter.AcceptChangesDuringUpdate = false;
            _adapter.Connection = Connection;
            _adapter.CommandTimeout = CommandTimeout;

            var entriesAffected = await ExecuteInTransactionAsync(
                () => _adapter.UpdateAsync(ObjectStateManager, cancellationToken), throwOnExistingTransaction,
                                            /*startLocalTransaction:*/ true, /*releaseConnectionOnSuccess:*/ true, cancellationToken)
                                            .ConfigureAwait(continueOnCapturedContext: false);

            if ((SaveOptions.AcceptAllChangesAfterSave & options) != 0)
            {
                try
                {
                    AcceptAllChanges();
                }
                catch (Exception e)
                {
                    // If AcceptAllChanges throws - let's inform user that changes in database were committed 
                    // and that Context and Database can be in inconsistent state.
                    throw new InvalidOperationException(Strings.ObjectContext_AcceptAllChangesFailure(e.Message));
                }
            }

            return entriesAffected;
        }

#endif

        #endregion //SaveChanges

        /// <summary>
        ///     Executes a function in a local transaction and returns the result.
        /// </summary>
        /// <remarks>
        ///     A local transaction is created only if there are no existing local nor ambient transactions.
        ///     This method will ensure that the connection is opened and release it if an exception is thrown.
        ///     The caller is responsible of releasing the connection if no exception is thrown, unless
        ///     <paramref name="releaseConnectionOnSuccess"/> is set to <c>true</c>.
        /// </remarks>
        /// <typeparam name="T"> Type of the result. </typeparam>
        /// <param name="func"> The function to invoke. </param>
        /// <param name="throwOnExistingTransaction"> Whether to throw on an existing transaction. </param>
        /// <param name="startLocalTransaction"> Whether should start a new local transaction when there's no existing one. </param>
        /// <param name="releaseConnectionOnSucces"> Whether the connection will also be released when no exceptions are thrown. </param>
        /// <returns> The result from invoking <paramref name="func"/>. </returns>
        internal virtual T ExecuteInTransaction<T>(
            Func<T> func, bool throwOnExistingTransaction, bool startLocalTransaction, bool releaseConnectionOnSuccess)
        {
            EnsureConnection();

            var needLocalTransaction = false;
            var connection = (EntityConnection)Connection;
            if (connection.CurrentTransaction == null
                && !connection.EnlistedInUserTransaction
                && _lastTransaction == null)
            {
                needLocalTransaction = startLocalTransaction;
            }
            else if (throwOnExistingTransaction)
            {
                throw Error.ExecutionStrategy_ExistingTransaction();
            }
            // else the caller already has his own local transaction going; caller will do the abort or commit.

            DbTransaction localTransaction = null;
            try
            {
                // EntityConnection tracks the CurrentTransaction we don't need to pass it around
                if (needLocalTransaction)
                {
                    localTransaction = connection.BeginTransaction();
                }

                var result = func();

                if (localTransaction != null)
                {
                    // we started the local transaction; so we also commit it
                    localTransaction.Commit();
                }
                // else on success with no exception is thrown, caller generally commits the transaction

                if (releaseConnectionOnSuccess)
                {
                    ReleaseConnection();
                }

                return result;
            }
            catch (Exception)
            {
                ReleaseConnection();
                throw;
            }
            finally
            {
                if (localTransaction != null)
                {
                    // we started the local transaction; so it requires disposal (rollback if not previously committed
                    localTransaction.Dispose();
                }
                // else on failure with an exception being thrown, caller generally aborts (default action with transaction without an explict commit)
            }
        }

#if !NET40

        /// <summary>
        ///     An asynchronous version of ExecuteStoreQuery, which
        ///     executes a function in a local transaction and returns the result.
        /// </summary>
        /// <remarks>
        ///     A local transaction is created only if there are no existing local nor ambient transactions.
        ///     This method will ensure that the connection is opened and release it if an exception is thrown.
        ///     The caller is responsible of releasing the connection if no exception is thrown, unless
        ///     <paramref name="releaseConnectionOnSuccess"/> is set to <c>true</c>.
        /// </remarks>
        /// <typeparam name="T"> Type of the result. </typeparam>
        /// <param name="func"> The function to invoke. </param>
        /// <param name="throwOnExistingTransaction"> Whether to throw on an existing transaction. </param>
        /// <param name="startLocalTransaction"> Whether should start a new local transaction when there's no existing one. </param>
        /// <param name="releaseConnectionOnSuccess"> Whether the connection will also be released when no exceptions are thrown. </param>
        /// <param name="cancellationToken"> The token to monitor for cancellation requests. </param>
        /// <returns> A task containing the result from invoking <paramref name="func"/>. </returns>
        internal virtual async Task<T> ExecuteInTransactionAsync<T>(
            Func<Task<T>> func, bool throwOnExistingTransaction,
            bool startLocalTransaction, bool releaseConnectionOnSuccess, CancellationToken cancellationToken)
        {
            await EnsureConnectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            var needLocalTransaction = false;
            var connection = (EntityConnection)Connection;
            if (connection.CurrentTransaction == null
                && !connection.EnlistedInUserTransaction
                && _lastTransaction == null)
            {
                needLocalTransaction = startLocalTransaction;
            }
            else if (throwOnExistingTransaction)
            {
                throw Error.ExecutionStrategy_ExistingTransaction();
            }
            // else the caller already has his own local transaction going; caller will do the abort or commit.

            DbTransaction localTransaction = null;
            try
            {
                // EntityConnection tracks the CurrentTransaction we don't need to pass it around
                if (needLocalTransaction)
                {
                    localTransaction = connection.BeginTransaction();
                }

                var result = await func().ConfigureAwait(continueOnCapturedContext: false);

                if (localTransaction != null)
                {
                    // we started the local transaction; so we also commit it
                    localTransaction.Commit();
                }
                // else on success with no exception is thrown, caller generally commits the transaction
                
                if (releaseConnectionOnSuccess)
                {
                    ReleaseConnection();
                }

                return result;
            }
            catch (Exception)
            {
                ReleaseConnection();
                throw;
            }
            finally
            {
                if (localTransaction != null)
                {
                    // we started the local transaction; so it requires disposal (rollback if not previously committed
                    localTransaction.Dispose();
                }
                // else on failure with an exception being thrown, caller generally aborts (default action with transaction without an explict commit)
            }
        }

#endif

        /// <summary>
        ///     For every tracked entity which doesn't implement IEntityWithChangeTracker detect changes in the entity's property values
        ///     and marks appropriate ObjectStateEntry as Modified.
        ///     For every tracked entity which doesn't implement IEntityWithRelationships detect changes in its relationships.
        ///     The method is used interanally by ObjectContext.SaveChanges() but can be also used if user wants to detect changes
        ///     and have ObjectStateEntries in appropriate state before the SaveChanges() method is called.
        /// </summary>
        public virtual void DetectChanges()
        {
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
            ObjectStateManager.DetectChanges();
            ObjectStateManager.AssertAllForeignKeyIndexEntriesAreValid();
        }

        /// <summary>
        ///     Attempts to retrieve an object from the cache or the store.
        /// </summary>
        /// <param name="key"> Key of the object to be found. </param>
        /// <param name="value"> Out param for the object. </param>
        /// <returns> True if the object was found, false otherwise. </returns>
        [SuppressMessage("Microsoft.Design", "CA1007:UseGenericsWhereAppropriate")]
        public virtual bool TryGetObjectByKey(EntityKey key, out object value)
        {
            // try the cache first
            EntityEntry entry;
            ObjectStateManager.TryGetEntityEntry(key, out entry); // this will check key argument
            if (entry != null)
            {
                // can't find keys
                if (!entry.IsKeyEntry)
                {
                    // SQLBUDT 511296 returning deleted object.
                    value = entry.Entity;
                    return value != null;
                }
            }

            if (key.IsTemporary)
            {
                // If the key is temporary, we cannot find a corresponding object in the store.
                value = null;
                return false;
            }

            var entitySet = key.GetEntitySet(MetadataWorkspace);
            Debug.Assert(entitySet != null, "Key's EntitySet should not be null in the MetadataWorkspace");

            // Validate the EntityKey values against the EntitySet
            key.ValidateEntityKey(_workspace, entitySet, true /*isArgumentException*/, "key");

            // Ensure the assembly containing the entity's CLR type is loaded into the workspace.
            // If the schema types are not loaded: metadata, cache & query would be unable to reason about the type.
            // Either the entity type's assembly is already in the ObjectItemCollection or we auto-load the user's calling assembly and its referenced assemblies.
            // *GetCallingAssembly returns the assembly of the method that invoked the currently executing method.
            MetadataWorkspace.ImplicitLoadFromEntityType(entitySet.ElementType, Assembly.GetCallingAssembly());

            // Execute the query:
            // SELECT VALUE X FROM [EC].[ES] AS X
            // WHERE X.KeyProp0 = @p0 AND X.KeyProp1 = @p1 AND ... 
            // parameters are the key values 

            // Build the Entity SQL query
            var esql = new StringBuilder();
            esql.AppendFormat(
                "SELECT VALUE X FROM {0}.{1} AS X WHERE ", EntityUtil.QuoteIdentifier(entitySet.EntityContainer.Name),
                EntityUtil.QuoteIdentifier(entitySet.Name));
            var members = key.EntityKeyValues;
            var keyMembers = entitySet.ElementType.KeyMembers;
            var parameters = new ObjectParameter[members.Length];

            for (var i = 0; i < members.Length; i++)
            {
                if (i > 0)
                {
                    esql.Append(" AND ");
                }

                var parameterName = string.Format(CultureInfo.InvariantCulture, "p{0}", i.ToString(CultureInfo.InvariantCulture));
                esql.AppendFormat("X.{0} = @{1}", EntityUtil.QuoteIdentifier(members[i].Key), parameterName);
                parameters[i] = new ObjectParameter(parameterName, members[i].Value);

                // Try to set the TypeUsage on the ObjectParameter
                EdmMember keyMember = null;
                if (keyMembers.TryGetValue(members[i].Key, true, out keyMember))
                {
                    parameters[i].TypeUsage = keyMember.TypeUsage;
                }
            }

            // Execute the query
            object entity = null;
            var results = CreateQuery<object>(esql.ToString(), parameters).Execute(MergeOption.AppendOnly);
            foreach (var queriedEntity in results)
            {
                Debug.Assert(entity == null, "Query for a key returned more than one entity!");
                entity = queriedEntity;
            }

            value = entity;
            return value != null;
        }

        /// <summary>
        ///     Executes the given function on the default container.
        /// </summary>
        /// <typeparam name="TElement"> Element type for function results. </typeparam>
        /// <param name="functionName">
        ///     Name of function. May include container (e.g. ContainerName.FunctionName) or just function name when DefaultContainerName is known.
        /// </param>
        /// <param name="parameters"> The parameter values to use for the function. </param>
        /// <exception cref="ArgumentException"> If function is null or empty </exception>
        /// <exception cref="InvalidOperationException">
        ///     If function is invalid (syntax,
        ///     does not exist, refers to a function with return type incompatible with T)
        /// </exception>
        public ObjectResult<TElement> ExecuteFunction<TElement>(string functionName, params ObjectParameter[] parameters)
        {
            Check.NotNull(parameters, "parameters");

            return ExecuteFunction<TElement>(functionName, MergeOption.AppendOnly, parameters);
        }

        /// <summary>
        ///     Executes the given function on the default container.
        /// </summary>
        /// <typeparam name="TElement"> Element type for function results. </typeparam>
        /// <param name="functionName">
        ///     Name of function. May include container (e.g. ContainerName.FunctionName) or just function name when DefaultContainerName is known.
        /// </param>
        /// <param name="mergeOption"> Merge option to use for entity results. </param>
        /// <param name="parameters"> The parameter values to use for the function. </param>
        /// <exception cref="ArgumentException">If function is null or empty</exception>
        /// <exception cref="InvalidOperationException">
        ///     If function is invalid (syntax,
        ///     does not exist, refers to a function with return type incompatible with T)
        /// </exception>
        public virtual ObjectResult<TElement> ExecuteFunction<TElement>(
            string functionName, MergeOption mergeOption, params ObjectParameter[] parameters)
        {
            Check.NotNull(parameters, "parameters");
            Check.NotEmpty(functionName, "function");
            return ExecuteFunction<TElement>(functionName, new ExecutionOptions(mergeOption, streaming: false), parameters);
        }

        /// <summary>
        ///     Executes the given function on the default container.
        /// </summary>
        /// <typeparam name="TElement"> Element type for function results. </typeparam>
        /// <param name="functionName">
        ///     Name of function. May include container (e.g. ContainerName.FunctionName) or just function name when DefaultContainerName is known.
        /// </param>
        /// <param name="executionOptions"> The options for executing this function. </param>
        /// <param name="parameters"> The parameter values to use for the function. </param>
        /// <exception cref="ArgumentException"> If function is null or empty </exception>
        /// <exception cref="InvalidOperationException">
        ///     If function is invalid (syntax,
        ///     does not exist, refers to a function with return type incompatible with T)
        /// </exception>
        public virtual ObjectResult<TElement> ExecuteFunction<TElement>(
            string functionName, ExecutionOptions executionOptions, params ObjectParameter[] parameters)
        {
            Check.NotNull(parameters, "parameters");
            Check.NotEmpty(functionName, "function");

            EdmFunction functionImport;
            var entityCommand = CreateEntityCommandForFunctionImport(functionName, out functionImport, parameters);
            var returnTypeCount = Math.Max(1, functionImport.ReturnParameters.Count);
            var expectedEdmTypes = new EdmType[returnTypeCount];
            expectedEdmTypes[0] = MetadataHelper.GetAndCheckFunctionImportReturnType<TElement>(functionImport, 0, MetadataWorkspace);
            for (var i = 1; i < returnTypeCount; i++)
            {
                if (!MetadataHelper.TryGetFunctionImportReturnType(functionImport, i, out expectedEdmTypes[i]))
                {
                    throw EntityUtil.ExecuteFunctionCalledWithNonReaderFunction(functionImport);
                }
            }

            var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);

            if (executionStrategy.RetriesOnFailure
                && executionOptions.Streaming)
            {
                throw new InvalidOperationException(Strings.ExecutionStrategy_StreamingNotSupported);
            }

            return executionStrategy.Execute(
                () => ExecuteInTransaction(
                    () => CreateFunctionObjectResult<TElement>(entityCommand, functionImport.EntitySets, expectedEdmTypes, executionOptions),
                    throwOnExistingTransaction: executionStrategy.RetriesOnFailure, startLocalTransaction: true,
                    releaseConnectionOnSuccess: !executionOptions.Streaming));
        }

        /// <summary>
        ///     Executes the given function on the default container and discard any results returned from the function.
        /// </summary>
        /// <param name="functionName">
        ///     Name of function. May include container (e.g. ContainerName.FunctionName) or just function name when DefaultContainerName is known.
        /// </param>
        /// <param name="parameters"> The parameter values to use for the function. </param>
        /// <returns> Number of rows affected </returns>
        /// <exception cref="ArgumentException">If function is null or empty</exception>
        /// <exception cref="InvalidOperationException">
        ///     If function is invalid (syntax,
        ///     does not exist, refers to a function with return type incompatible with T)
        /// </exception>
        public virtual int ExecuteFunction(string functionName, params ObjectParameter[] parameters)
        {
            Check.NotNull(parameters, "parameters");
            Check.NotEmpty(functionName, "function");

            EdmFunction functionImport;
            var entityCommand = CreateEntityCommandForFunctionImport(functionName, out functionImport, parameters);

            var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);
            return executionStrategy.Execute(
                () => ExecuteInTransaction(
                    () => ExecuteFunctionCommand(entityCommand), throwOnExistingTransaction: executionStrategy.RetriesOnFailure,
                    startLocalTransaction: true, releaseConnectionOnSuccess: true));
        }

        private static int ExecuteFunctionCommand(EntityCommand entityCommand)
        {
            // Prepare the command before calling ExecuteNonQuery, so that exceptions thrown during preparation are not wrapped in EntityCommandExecutionException
            entityCommand.Prepare();

            try
            {
                return entityCommand.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                if (e.IsCatchableEntityExceptionType())
                {
                    throw new EntityCommandExecutionException(Strings.EntityClient_CommandExecutionFailed, e);
                }

                throw;
            }
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        private EntityCommand CreateEntityCommandForFunctionImport(
            string functionName, out EdmFunction functionImport, params ObjectParameter[] parameters)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (null == parameter)
                {
                    throw new InvalidOperationException(Strings.ObjectContext_ExecuteFunctionCalledWithNullParameter(i));
                }
            }

            string containerName;
            string functionImportName;

            functionImport =
                MetadataHelper.GetFunctionImport(
                    functionName, DefaultContainerName, MetadataWorkspace,
                    out containerName, out functionImportName);

            var connection = (EntityConnection)Connection;

            // create query
            var entityCommand = new EntityCommand();
            entityCommand.CommandType = CommandType.StoredProcedure;
            entityCommand.CommandText = containerName + "." + functionImportName;
            entityCommand.Connection = connection;
            if (CommandTimeout.HasValue)
            {
                entityCommand.CommandTimeout = CommandTimeout.Value;
            }

            PopulateFunctionImportEntityCommandParameters(parameters, functionImport, entityCommand);

            return entityCommand;
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Reader disposed by the returned ObjectResult")]
        private ObjectResult<TElement> CreateFunctionObjectResult<TElement>(
            EntityCommand entityCommand, ReadOnlyMetadataCollection<EntitySet> entitySets, EdmType[] edmTypes,
            ExecutionOptions executionOptions)
        {
            DebugCheck.NotNull(edmTypes);
            Debug.Assert(edmTypes.Length > 0);

            var commandDefinition = entityCommand.GetCommandDefinition();

            // get store data reader
            DbDataReader storeReader = null;
            try
            {
                storeReader = commandDefinition.ExecuteStoreCommands(entityCommand, CommandBehavior.Default);
            }
            catch (Exception e)
            {
                if (e.IsCatchableEntityExceptionType())
                {
                    throw new EntityCommandExecutionException(Strings.EntityClient_CommandExecutionFailed, e);
                }

                throw;
            }

            if (executionOptions.Streaming)
            {
                return MaterializedDataRecord<TElement>(
                    entityCommand, storeReader, 0, entitySets, edmTypes, executionOptions.MergeOption, useSpatialReader: true,
                    shouldReleaseConnection: true);
            }
            else
            {
                BufferedDataReader bufferedReader = null;
                try
                {
                    var storeItemCollection = (StoreItemCollection)MetadataWorkspace.GetItemCollection(DataSpace.SSpace);
                    var providerServices = DbConfiguration.GetService<DbProviderServices>(storeItemCollection.StoreProviderInvariantName);

                    bufferedReader = new BufferedDataReader(storeReader);
                    bufferedReader.Initialize(storeItemCollection.StoreProviderManifestToken, providerServices);
                }
                catch (Exception e)
                {
                    if (bufferedReader != null)
                    {
                        bufferedReader.Dispose();
                    }

                    if (e.IsCatchableEntityExceptionType())
                    {
                        throw new EntityCommandExecutionException(Strings.EntityClient_CommandExecutionFailed, e);
                    }

                    throw;
                }

                return MaterializedDataRecord<TElement>(
                    entityCommand, bufferedReader, 0, entitySets, edmTypes, executionOptions.MergeOption, useSpatialReader: false,
                    shouldReleaseConnection: false);
            }
        }

        /// <summary>
        ///     Get the materializer for the resultSetIndexth result set of storeReader.
        /// </summary>
        internal ObjectResult<TElement> MaterializedDataRecord<TElement>(
            EntityCommand entityCommand, DbDataReader storeReader, int resultSetIndex, ReadOnlyMetadataCollection<EntitySet> entitySets,
            EdmType[] edmTypes, MergeOption mergeOption, bool useSpatialReader, bool shouldReleaseConnection)
        {
            DebugCheck.NotNull(entityCommand);
            DebugCheck.NotNull(storeReader);
            DebugCheck.NotNull(entitySets);
            DebugCheck.NotNull(edmTypes);

            var commandDefinition = entityCommand.GetCommandDefinition();
            try
            {
                // We want the shaper to close the reader if it is the last result set.
                var shaperOwnsReader = edmTypes.Length <= resultSetIndex + 1;

                //Note: Defensive check for historic reasons, we expect entitySets.Count > resultSetIndex 
                var entitySet = entitySets.Count > resultSetIndex ? entitySets[resultSetIndex] : null;

                // create the shaper
                var cacheManager = Perspective.MetadataWorkspace.GetQueryCacheManager();
                var shaperFactory = _translator.TranslateColumnMap<TElement>(
                    cacheManager, commandDefinition.CreateColumnMap(storeReader, resultSetIndex), MetadataWorkspace, null, mergeOption,
                    false);
                var shaper = shaperFactory.Create(
                    storeReader, this, MetadataWorkspace, mergeOption, shaperOwnsReader, useSpatialReader, shouldReleaseConnection);

                NextResultGenerator nextResultGenerator;

                // We need to run notifications when the data reader is closed in order to propagate any out parameters.
                // We do this whenever the last (declared) result set's enumerator is disposed (this calls Finally on the shaper)
                // or when the underlying reader is closed as a result of the ObjectResult itself getting disposed.   
                // We use onReaderDisposeHasRun to ensure that this notification is only called once.   
                // the alternative approach of not making the final ObjectResult's disposal result do cleanup doesn't work in the case where
                // its GetEnumerator is called explicitly, and the resulting enumerator is never disposed.
                var onReaderDisposeHasRun = false;
                Action<object, EventArgs> onReaderDispose = (object sender, EventArgs e) =>
                                                                {
                                                                    if (!onReaderDisposeHasRun)
                                                                    {
                                                                        onReaderDisposeHasRun = true;
                                                                        // consume the store reader
                                                                        CommandHelper.ConsumeReader(storeReader);
                                                                        // trigger event callback
                                                                        entityCommand.NotifyDataReaderClosing();
                                                                    }
                                                                };

                if (shaperOwnsReader)
                {
                    shaper.OnDone += new EventHandler(onReaderDispose);
                    nextResultGenerator = null;
                }
                else
                {
                    nextResultGenerator = new NextResultGenerator(
                        this, entityCommand, edmTypes, entitySets, mergeOption, resultSetIndex + 1);
                }

                // We want the ObjectResult to close the reader in its Dispose method, even if it is not the last result set.
                // This is to allow users to cancel reading results without the unnecessary iteration thru all the result sets.
                return new ObjectResult<TElement>(
                    shaper, entitySet, TypeUsage.Create(edmTypes[resultSetIndex]), true, shouldReleaseConnection, nextResultGenerator,
                    onReaderDispose);
            }
            catch
            {
                ReleaseConnection();
                storeReader.Dispose();
                throw;
            }
        }

        private void PopulateFunctionImportEntityCommandParameters(
            ObjectParameter[] parameters, EdmFunction functionImport, EntityCommand command)
        {
            // attach entity parameters
            for (var i = 0; i < parameters.Length; i++)
            {
                var objectParameter = parameters[i];
                var entityParameter = new EntityParameter();

                var functionParameter = FindParameterMetadata(functionImport, parameters, i);

                if (null != functionParameter)
                {
                    entityParameter.Direction = MetadataHelper.ParameterModeToParameterDirection(
                        functionParameter.Mode);
                    entityParameter.ParameterName = functionParameter.Name;
                }
                else
                {
                    entityParameter.ParameterName = objectParameter.Name;
                }

                entityParameter.Value = objectParameter.Value ?? DBNull.Value;

                if (DBNull.Value == entityParameter.Value
                    || entityParameter.Direction != ParameterDirection.Input)
                {
                    TypeUsage typeUsage;
                    if (functionParameter != null)
                    {
                        // give precedence to the statically declared type usage
                        typeUsage = functionParameter.TypeUsage;
                    }
                    else if (null == objectParameter.TypeUsage)
                    {
                        Debug.Assert(objectParameter.MappableType != null, "MappableType must not be null");
                        Debug.Assert(Nullable.GetUnderlyingType(objectParameter.MappableType) == null, "Nullable types not expected here.");

                        // since ObjectParameters do not allow users to especify 'facets', make 
                        // sure that the parameter typeusage is not populated with the provider
                        // dafault facet values.
                        // Try getting the type from the workspace. This may fail however for one of the following reasons:
                        // - the type is not a model type
                        // - the types were not loaded into the workspace yet
                        // If the types were not loaded into the workspace we try loading types from the assembly the type lives in and re-try
                        // loading the type. We don't care if the type still cannot be loaded - in this case the result TypeUsage will be null
                        // which we handle later.
                        if (!Perspective.TryGetTypeByName(objectParameter.MappableType.FullName, /*ignoreCase */ false, out typeUsage))
                        {
                            MetadataWorkspace.ImplicitLoadAssemblyForType(objectParameter.MappableType, null);
                            Perspective.TryGetTypeByName(objectParameter.MappableType.FullName, /*ignoreCase */ false, out typeUsage);
                        }
                    }
                    else
                    {
                        typeUsage = objectParameter.TypeUsage;
                    }

                    // set type information (if the provider cannot determine it from the actual value)
                    EntityCommandDefinition.PopulateParameterFromTypeUsage(
                        entityParameter, typeUsage, entityParameter.Direction != ParameterDirection.Input);
                }

                if (entityParameter.Direction
                    != ParameterDirection.Input)
                {
                    var binder = new ParameterBinder(entityParameter, objectParameter);
                    command.OnDataReaderClosing += binder.OnDataReaderClosingHandler;
                }

                command.Parameters.Add(entityParameter);
            }
        }

        private static FunctionParameter FindParameterMetadata(EdmFunction functionImport, ObjectParameter[] parameters, int ordinal)
        {
            // Retrieve parameter information from functionImport.
            // We first attempt to resolve by case-sensitive name. If there is no exact match,
            // check if there is a case-insensitive match. Case insensitive matches are only permitted
            // when a single parameter would match.
            FunctionParameter functionParameter;
            var parameterName = parameters[ordinal].Name;
            if (!functionImport.Parameters.TryGetValue(parameterName, false, out functionParameter))
            {
                // if only one parameter has this name, try a case-insensitive lookup
                var matchCount = 0;
                for (var i = 0; i < parameters.Length && matchCount < 2; i++)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(parameters[i].Name, parameterName))
                    {
                        matchCount++;
                    }
                }

                if (matchCount == 1)
                {
                    functionImport.Parameters.TryGetValue(parameterName, true, out functionParameter);
                }
            }

            return functionParameter;
        }

        /// <summary>
        ///     Attempt to generate a proxy type for each type in the supplied enumeration.
        /// </summary>
        /// <param name="types"> Enumeration of Type objects that should correspond to O-Space types. </param>
        /// <remarks>
        ///     Types in the enumeration that do not map to an O-Space type are ignored.
        ///     Also, there is no guarantee that a proxy type will be created for a given type,
        ///     only that if a proxy can be generated, then it will be generated.
        ///     See <see cref="EntityProxyFactory" /> class for more information about proxy type generation.
        /// </remarks>
        public virtual void CreateProxyTypes(IEnumerable<Type> types)
        {
            var ospaceItems = (ObjectItemCollection)MetadataWorkspace.GetItemCollection(DataSpace.OSpace);

            // Ensure metadata is loaded for each type,
            // and attempt to create proxy type only for types that have a mapping to an O-Space EntityType.
            EntityProxyFactory.TryCreateProxyTypes(
                types.Select(
                    type =>
                        {
                            // Ensure the assembly containing the entity's CLR type is loaded into the workspace.
                            MetadataWorkspace.ImplicitLoadAssemblyForType(type, null);

                            EntityType entityType;
                            ospaceItems.TryGetItem(type.FullName, out entityType);
                            return entityType;
                        }).Where(entityType => entityType != null)
                );
        }

        /// <summary>
        ///     Return an enumerable of the current set of CLR proxy types.
        /// </summary>
        /// <returns> Enumerable of the current set of CLR proxy types. This will never be null. </returns>
        [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        public static IEnumerable<Type> GetKnownProxyTypes()
        {
            return EntityProxyFactory.GetKnownProxyTypes();
        }

        /// <summary>
        ///     Given a type that may represent a known proxy type,
        ///     return the corresponding type being proxied.
        /// </summary>
        /// <param name="type"> Type that may represent a proxy type. </param>
        /// <returns> Non-proxy type that corresponds to the supplied proxy type, or the supplied type if it is not a known proxy type. </returns>
        /// <exception cref="ArgumentNullException">If the value of the type parameter is null.</exception
        public static Type GetObjectType(Type type)
        {
            Check.NotNull(type, "type");

            return EntityProxyFactory.IsProxyType(type) ? type.BaseType : type;
        }

        /// <summary>
        ///     Create an appropriate instance of the type <typeparamref name="T" />.
        /// </summary>
        /// <typeparam name="T"> Type of object to be returned. </typeparam>
        /// <returns>
        ///     An instance of an object of type <typeparamref name="T" /> . The object will either be an instance of the exact type
        ///     <typeparamref
        ///         name="T" />
        ///     , or possibly an instance of the proxy type that corresponds to <typeparamref name="T" /> .
        /// </returns>
        /// <remarks>
        ///     The type <typeparamref name="T" /> must have an OSpace EntityType representation.
        /// </remarks>
        public virtual T CreateObject<T>()
            where T : class
        {
            T instance = null;
            var clrType = typeof(T);

            // Ensure the assembly containing the entity's CLR type is loaded into the workspace.
            MetadataWorkspace.ImplicitLoadAssemblyForType(clrType, null);

            // Retrieve the OSpace EntityType that corresponds to the supplied CLR type.
            // This call ensure that this mapping exists.
            var entityType = MetadataWorkspace.GetItem<ClrEntityType>(clrType.FullName, DataSpace.OSpace);
            EntityProxyTypeInfo proxyTypeInfo = null;

            if (ContextOptions.ProxyCreationEnabled
                && ((proxyTypeInfo = EntityProxyFactory.GetProxyType(entityType)) != null))
            {
                instance = (T)proxyTypeInfo.CreateProxyObject();

                // After creating the proxy we need to add additional state to the proxy such
                // that it is able to function correctly when returned.  In particular, it needs
                // an initialized set of RelatedEnd objects because it will not be possible to
                // create these for convention based mapping once the metadata in the context has
                // been lost.
                var wrappedEntity = EntityWrapperFactory.CreateNewWrapper(instance, null);
                wrappedEntity.InitializingProxyRelatedEnds = true;
                try
                {
                    // We're setting the context temporarily here so that we can go through the process
                    // of creating RelatedEnds even with convention-based mapping.
                    // However, we also need to tell the wrapper that we're doing this so that we don't
                    // try to do things that we normally do when we have a context, such as adding the
                    // context to the RelatedEnds.  We can't do these things since they require an
                    // EntitySet, and, because of MEST, we don't have one.
                    wrappedEntity.AttachContext(this, null, MergeOption.NoTracking);
                    proxyTypeInfo.SetEntityWrapper(wrappedEntity);
                    if (proxyTypeInfo.InitializeEntityCollections != null)
                    {
                        proxyTypeInfo.InitializeEntityCollections.Invoke(null, new object[] { wrappedEntity });
                    }
                }
                finally
                {
                    wrappedEntity.InitializingProxyRelatedEnds = false;
                    wrappedEntity.DetachContext();
                }
            }
            else
            {
                instance = DelegateFactory.GetConstructorDelegateForType(entityType)() as T;
            }

            return instance;
        }

        /// <summary>
        ///     Execute a command against the database server that does not return a sequence of objects.
        ///     The command is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <param name="commandText"> The command specified in the server's native query language. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns> A single integer return value </returns>
        public virtual int ExecuteStoreCommand(string commandText, params object[] parameters)
        {
            var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);
            return executionStrategy.Execute(
                () => ExecuteInTransaction(
                    () => CreateStoreCommand(commandText, parameters).ExecuteNonQuery(),
                    throwOnExistingTransaction: executionStrategy.RetriesOnFailure, startLocalTransaction: true,
                    releaseConnectionOnSuccess: true));
        }

#if !NET40

        /// <summary>
        ///     An asynchronous version of ExecuteStoreCommand, which
        ///     executes a command against the database server that does not return a sequence of objects.
        ///     The command is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <param name="commandText"> The command specified in the server's native query language. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns> A Task containing a single integer return value. </returns>
        public Task<int> ExecuteStoreCommandAsync(string commandText, params object[] parameters)
        {
            return ExecuteStoreCommandAsync(commandText, CancellationToken.None, parameters);
        }

        /// <summary>
        ///     An asynchronous version of ExecuteStoreCommand, which
        ///     executes a command against the database server that does not return a sequence of objects.
        ///     The command is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <param name="commandText"> The command specified in the server's native query language. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <param name="cancellationToken"> The token to monitor for cancellation requests. </param>
        /// <returns> A Task containing a single integer return value. </returns>
        public virtual Task<int> ExecuteStoreCommandAsync(
            string commandText, CancellationToken cancellationToken, params object[] parameters)
        {
            var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);
            return executionStrategy.ExecuteAsync(
                () => ExecuteInTransactionAsync(
                    () => CreateStoreCommand(commandText, parameters).ExecuteNonQueryAsync(cancellationToken),
                          /*throwOnExistingTransaction:*/ executionStrategy.RetriesOnFailure,
                          /*startLocalTransaction:*/ true, /*releaseConnectionOnSuccess:*/ true, cancellationToken),
                cancellationToken);
        }

#endif

        /// <summary>
        ///     Execute the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the result sequence. </typeparam>
        /// <param name="commandText"> The query specified in the server's native query language. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     An enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        public virtual ObjectResult<TElement> ExecuteStoreQuery<TElement>(string commandText, params object[] parameters)
        {
            return ExecuteStoreQueryReliably<TElement>(
                commandText, /*entitySetName:*/null, ExecutionOptions.Default, parameters);
        }

        /// <summary>
        ///     Execute the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the result sequence. </typeparam>
        /// <param name="commandText"> The query specified in the server's native query language. </param>
        /// <param name="executionOptions"> The options for executing this query. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     An enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        public virtual ObjectResult<TElement> ExecuteStoreQuery<TElement>(
            string commandText, ExecutionOptions executionOptions, params object[] parameters)
        {
            return ExecuteStoreQueryReliably<TElement>(
                commandText, /*entitySetName:*/null, executionOptions, parameters);
        }

        /// <summary>
        ///     Execute the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the resulting sequence </typeparam>
        /// <param name="commandText"> The DbDataReader to translate </param>
        /// <param name="entitySetName"> The entity set in which results should be tracked. Null indicates there is no entity set. </param>
        /// <param name="mergeOption"> Merge option to use for entity results. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     An enumeration of objects of type <typeparamref name="TElement" />.
        /// </returns>
        public virtual ObjectResult<TElement> ExecuteStoreQuery<TElement>(
            string commandText, string entitySetName, MergeOption mergeOption, params object[] parameters)
        {
            Check.NotEmpty(entitySetName, "entitySetName");
            return ExecuteStoreQueryReliably<TElement>(
                commandText, entitySetName, new ExecutionOptions(mergeOption, streaming: false), parameters);
        }

        /// <summary>
        ///     Execute the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the resulting sequence </typeparam>
        /// <param name="commandText"> The DbDataReader to translate </param>
        /// <param name="entitySetName"> The entity set in which results should be tracked. Null indicates there is no entity set. </param>
        /// <param name="executionOptions"> The options for executing this query. </param> 
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     An enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        public virtual ObjectResult<TElement> ExecuteStoreQuery<TElement>(
            string commandText, string entitySetName, ExecutionOptions executionOptions, params object[] parameters)
        {
            Check.NotEmpty(entitySetName, "entitySetName");
            return ExecuteStoreQueryReliably<TElement>(commandText, entitySetName, executionOptions, parameters);
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Buffer disposed by the returned ObjectResult")]
        private ObjectResult<TElement> ExecuteStoreQueryReliably<TElement>(
            string commandText, string entitySetName, ExecutionOptions executionOptions, params object[] parameters)
        {
            // Ensure the assembly containing the entity's CLR type
            // is loaded into the workspace. If the schema types are not loaded
            // metadata, cache & query would be unable to reason about the type. We
            // either auto-load <TElement>'s assembly into the ObjectItemCollection or we
            // auto-load the user's calling assembly and its referenced assemblies.
            // If the entities in the user's result spans multiple assemblies, the
            // user must manually call LoadFromAssembly. *GetCallingAssembly returns
            // the assembly of the method that invoked the currently executing method.
            MetadataWorkspace.ImplicitLoadAssemblyForType(typeof(TElement), Assembly.GetCallingAssembly());

            var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);

            if (executionStrategy.RetriesOnFailure
                && executionOptions.Streaming)
            {
                throw new InvalidOperationException(Strings.ExecutionStrategy_StreamingNotSupported);
            }

            return executionStrategy.Execute(
                () => ExecuteInTransaction(
                    () => ExecuteStoreQueryInternal<TElement>(
                        commandText, entitySetName, executionOptions, parameters),
                    throwOnExistingTransaction: executionStrategy.RetriesOnFailure, startLocalTransaction: false,
                    releaseConnectionOnSuccess: !executionOptions.Streaming));
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by ObjectResult")]
        private ObjectResult<TElement> ExecuteStoreQueryInternal<TElement>(
            string commandText, string entitySetName, ExecutionOptions executionOptions, params object[] parameters)
        {
            DbDataReader reader = null;
            try
            {
                using (var command = CreateStoreCommand(commandText, parameters))
                {
                    reader = command.ExecuteReader();
                }
                if (executionOptions.Streaming)
                {
                    return InternalTranslate<TElement>(
                        reader, entitySetName, executionOptions.MergeOption, readerOwned: true,
                        shouldReleaseConnection: executionOptions.Streaming);
                }
            }
            catch
            {
                // We only release the connection and dispose the reader when there is an exception.
                // Otherwise, the ObjectResult is in charge of doing it.
                if (reader != null)
                {
                    reader.Dispose();
                }

                throw;
            }

            BufferedDataReader bufferedReader = null;
            try
            {
                var storeItemCollection = (StoreItemCollection)MetadataWorkspace.GetItemCollection(DataSpace.SSpace);
                var providerServices = DbConfiguration.GetService<DbProviderServices>(storeItemCollection.StoreProviderInvariantName);

                bufferedReader = new BufferedDataReader(reader);
                bufferedReader.Initialize(storeItemCollection.StoreProviderManifestToken, providerServices);
                return InternalTranslate<TElement>(
                    bufferedReader, entitySetName, executionOptions.MergeOption, readerOwned: true,
                    shouldReleaseConnection: !executionOptions.Streaming);
            }
            catch
            {
                if (bufferedReader != null)
                {
                    bufferedReader.Dispose();
                }

                throw;
            }
        }

#if !NET40

        /// <summary>
        ///     An asynchronous version of ExecuteStoreQuery, which
        ///     executes the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the result sequence. </typeparam>
        /// <param name="commandText"> The query specified in the server's native query language. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     A Task containing an enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public Task<ObjectResult<TElement>> ExecuteStoreQueryAsync<TElement>(string commandText, params object[] parameters)
        {
            return ExecuteStoreQueryAsync<TElement>(commandText, CancellationToken.None, parameters);
        }

        /// <summary>
        ///     An asynchronous version of ExecuteStoreQuery, which
        ///     executes the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the result sequence. </typeparam>
        /// <param name="commandText"> The query specified in the server's native query language. </param>
        /// <param name="cancellationToken"> The token to monitor for cancellation requests. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     A Task containing an enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public virtual Task<ObjectResult<TElement>> ExecuteStoreQueryAsync<TElement>(
            string commandText, CancellationToken cancellationToken, params object[] parameters)
        {
            return ExecuteStoreQueryReliablyAsync<TElement>(
                commandText, /*entitySetName:*/null, ExecutionOptions.Default, cancellationToken, parameters);
        }

        /// <summary>
        ///     An asynchronous version of ExecuteStoreQuery, which
        ///     executes the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the result sequence. </typeparam>
        /// <param name="commandText"> The query specified in the server's native query language. </param>
        /// <param name="executionOptions"> The options for executing this query. </param> 
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     A Task containing an enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public virtual Task<ObjectResult<TElement>> ExecuteStoreQueryAsync<TElement>(
            string commandText, ExecutionOptions executionOptions, params object[] parameters)
        {
            return ExecuteStoreQueryReliablyAsync<TElement>(
                commandText, /*entitySetName:*/null, executionOptions, CancellationToken.None, parameters);
        }

        /// <summary>
        ///     An asynchronous version of ExecuteStoreQuery, which
        ///     executes the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the result sequence. </typeparam>
        /// <param name="commandText"> The query specified in the server's native query language. </param>
        /// <param name="executionOptions"> The options for executing this query. </param> 
        /// <param name="cancellationToken"> The token to monitor for cancellation requests. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     A Task containing an enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public virtual Task<ObjectResult<TElement>> ExecuteStoreQueryAsync<TElement>(
            string commandText, ExecutionOptions executionOptions, CancellationToken cancellationToken, params object[] parameters)
        {
            return ExecuteStoreQueryReliablyAsync<TElement>(
                commandText, /*entitySetName:*/null, executionOptions, cancellationToken, parameters);
        }

        /// <summary>
        ///     An asynchronous version of ExecuteStoreQuery, which
        ///     execute the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the resulting sequence </typeparam>
        /// <param name="commandText"> The DbDataReader to translate </param>
        /// <param name="entitySetName"> The entity set in which results should be tracked. Null indicates there is no entity set. </param>
        /// <param name="executionOptions"> The options for executing this query. </param> 
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     A Task containing an enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public Task<ObjectResult<TElement>> ExecuteStoreQueryAsync<TElement>(
            string commandText, string entitySetName, ExecutionOptions executionOption, params object[] parameters)
        {
            return ExecuteStoreQueryAsync<TElement>(commandText, entitySetName, executionOption, CancellationToken.None, parameters);
        }

        /// <summary>
        ///     An asynchronous version of ExecuteStoreQuery, which
        ///     execute the sequence returning query against the database server.
        ///     The query is specified using the server's native query language, such as SQL.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the resulting sequence </typeparam>
        /// <param name="commandText"> The DbDataReader to translate </param>
        /// <param name="entitySetName"> The entity set in which results should be tracked. Null indicates there is no entity set. </param>
        /// <param name="executionOptions"> The options for executing this query. </param> 
        /// <param name="cancellationToken"> The token to monitor for cancellation requests. </param>
        /// <param name="parameters"> The parameter values to use for the query. </param>
        /// <returns>
        ///     A Task containing an enumeration of objects of type <typeparamref name="TElement" /> .
        /// </returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public virtual Task<ObjectResult<TElement>> ExecuteStoreQueryAsync<TElement>(
            string commandText, string entitySetName, ExecutionOptions executionOption, CancellationToken cancellationToken,
            params object[] parameters)
        {
            Check.NotEmpty(entitySetName, "entitySetName");

            return ExecuteStoreQueryReliablyAsync<TElement>(
                commandText, entitySetName, executionOption, cancellationToken, parameters);
        }

        private Task<ObjectResult<TElement>> ExecuteStoreQueryReliablyAsync<TElement>(
            string commandText, string entitySetName, ExecutionOptions executionOptions, CancellationToken cancellationToken,
            params object[] parameters)
        {
            // Ensure the assembly containing the entity's CLR type
            // is loaded into the workspace. If the schema types are not loaded
            // metadata, cache & query would be unable to reason about the type. We
            // either auto-load <TElement>'s assembly into the ObjectItemCollection or we
            // auto-load the user's calling assembly and its referenced assemblies.
            // If the entities in the user's result spans multiple assemblies, the
            // user must manually call LoadFromAssembly. *GetCallingAssembly returns
            // the assembly of the method that invoked the currently executing method.
            MetadataWorkspace.ImplicitLoadAssemblyForType(typeof(TElement), Assembly.GetCallingAssembly());

            var executionStrategy = DbProviderServices.GetExecutionStrategy(Connection);

            if (executionStrategy.RetriesOnFailure
                && executionOptions.Streaming)
            {
                throw new InvalidOperationException(Strings.ExecutionStrategy_StreamingNotSupported);
            }

            return executionStrategy.ExecuteAsync(
                () => ExecuteInTransactionAsync(
                    () => ExecuteStoreQueryInternalAsync<TElement>(
                        commandText, entitySetName, executionOptions, cancellationToken, parameters),
                          /*throwOnExistingTransaction:*/ executionStrategy.RetriesOnFailure,
                          /*startLocalTransaction:*/ false, /*releaseConnectionOnSuccess:*/ !executionOptions.Streaming, cancellationToken),
                cancellationToken);
        }

        private async Task<ObjectResult<TElement>> ExecuteStoreQueryInternalAsync<TElement>(
            string commandText, string entitySetName, ExecutionOptions executionOptions,
            CancellationToken cancellationToken, params object[] parameters)
        {
            DbDataReader reader = null;
            try
            {
                using (var command = CreateStoreCommand(commandText, parameters))
                {
                    reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
                if (executionOptions.Streaming)
                {
                    return InternalTranslate<TElement>(
                        reader, entitySetName, executionOptions.MergeOption, readerOwned: true,
                        shouldReleaseConnection: executionOptions.Streaming);
                }
            }
            catch
            {
                // We only release the connection and dispose the reader when there is an exception.
                // Otherwise, the ObjectResult is in charge of doing it.
                if (reader != null)
                {
                    reader.Dispose();
                }

                throw;
            }

            BufferedDataReader bufferedReader = null;
            try
            {
                var storeItemCollection = (StoreItemCollection)MetadataWorkspace.GetItemCollection(DataSpace.SSpace);
                var providerServices = DbConfiguration.GetService<DbProviderServices>(storeItemCollection.StoreProviderInvariantName);

                bufferedReader = new BufferedDataReader(reader);
                await bufferedReader.InitializeAsync(storeItemCollection.StoreProviderManifestToken, providerServices, cancellationToken)
                                    .ConfigureAwait(continueOnCapturedContext: false);
                return InternalTranslate<TElement>(
                    bufferedReader, entitySetName, executionOptions.MergeOption, readerOwned: true,
                    shouldReleaseConnection: !executionOptions.Streaming);
            }
            catch
            {
                if (bufferedReader != null)
                {
                    bufferedReader.Dispose();
                }

                throw;
            }
        }

#endif

        /// <summary>
        ///     Translates the data from a DbDataReader into sequence of objects.
        /// </summary>
        /// <typeparam name="TElement"> The element type of the resulting sequence. </typeparam>
        /// <param name="reader"> The DbDataReader to translate </param>
        /// <param name="mergeOption"> Merge option to use for entity results. </param>
        /// <returns> The translated sequence of objects. </returns>
        public virtual ObjectResult<TElement> Translate<TElement>(DbDataReader reader)
        {
            // Ensure the assembly containing the entity's CLR type
            // is loaded into the workspace. If the schema types are not loaded
            // metadata, cache & query would be unable to reason about the type. We
            // either auto-load <TElement>'s assembly into the ObjectItemCollection or we
            // auto-load the user's calling assembly and its referenced assemblies.
            // If the entities in the user's result spans multiple assemblies, the
            // user must manually call LoadFromAssembly. *GetCallingAssembly returns
            // the assembly of the method that invoked the currently executing method.
            MetadataWorkspace.ImplicitLoadAssemblyForType(typeof(TElement), Assembly.GetCallingAssembly());

            return InternalTranslate<TElement>(
                reader, null /*entitySetName*/, MergeOption.AppendOnly, readerOwned: false, shouldReleaseConnection: false);
        }

        /// <summary>
        ///     Translates the data from a DbDataReader into sequence of entities.
        /// </summary>
        /// <typeparam name="TEntity"> The element type of the resulting sequence </typeparam>
        /// <param name="reader"> The DbDataReader to translate </param>
        /// <param name="entitySetName"> The entity set in which results should be tracked. Null indicates there is no entity set. </param>
        /// <param name="mergeOption"> Merge option to use for entity results. </param>
        /// <returns> The translated sequence of objects </returns>
        [SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter",
            Justification = "Generic parameters are required for strong-typing of the return type.")]
        public virtual ObjectResult<TEntity> Translate<TEntity>(DbDataReader reader, string entitySetName, MergeOption mergeOption)
        {
            Check.NotEmpty(entitySetName, "entitySetName");

            // Ensure the assembly containing the entity's CLR type
            // is loaded into the workspace. If the schema types are not loaded
            // metadata, cache & query would be unable to reason about the type. We
            // either auto-load <TEntity>'s assembly into the ObjectItemCollection or we
            // auto-load the user's calling assembly and its referenced assemblies.
            // If the entities in the user's result spans multiple assemblies, the
            // user must manually call LoadFromAssembly. *GetCallingAssembly returns
            // the assembly of the method that invoked the currently executing method.
            MetadataWorkspace.ImplicitLoadAssemblyForType(typeof(TEntity), Assembly.GetCallingAssembly());

            return InternalTranslate<TEntity>(reader, entitySetName, mergeOption, readerOwned: false, shouldReleaseConnection: false);
        }

        private ObjectResult<TElement> InternalTranslate<TElement>(
            DbDataReader reader, string entitySetName,
            MergeOption mergeOption, bool readerOwned, bool shouldReleaseConnection)
        {
            DebugCheck.NotNull(reader);
            EntityUtil.CheckArgumentMergeOption(mergeOption);
            EntitySet entitySet = null;
            if (!string.IsNullOrEmpty(entitySetName))
            {
                entitySet = GetEntitySetFromName(entitySetName);
            }

            // get the expected EDM type
            EdmType modelEdmType;
            var unwrappedTElement = Nullable.GetUnderlyingType(typeof(TElement)) ?? typeof(TElement);
            CollectionColumnMap columnMap;
            // for enums that are not in the model we use the enum underlying type
            if (MetadataWorkspace.TryDetermineCSpaceModelType<TElement>(out modelEdmType)
                || (unwrappedTElement.IsEnum &&
                    MetadataWorkspace.TryDetermineCSpaceModelType(unwrappedTElement.GetEnumUnderlyingType(), out modelEdmType)))
            {
                if (entitySet != null
                    && !entitySet.ElementType.IsAssignableFrom(modelEdmType))
                {
                    throw new InvalidOperationException(
                        Strings.ObjectContext_InvalidEntitySetForStoreQuery(
                            entitySet.EntityContainer.Name,
                            entitySet.Name, typeof(TElement)));
                }

                columnMap = _columnMapFactory.CreateColumnMapFromReaderAndType(reader, modelEdmType, entitySet, null);
            }
            else
            {
                columnMap = _columnMapFactory.CreateColumnMapFromReaderAndClrType(reader, typeof(TElement), MetadataWorkspace);
            }

            // build a shaper for the column map to produce typed results
            var cacheManager = MetadataWorkspace.GetQueryCacheManager();
            var shaperFactory = _translator.TranslateColumnMap<TElement>(
                cacheManager, columnMap, MetadataWorkspace, null, mergeOption, false);
            var shaper = shaperFactory.Create(
                reader, this, MetadataWorkspace, mergeOption, readerOwned, /*useSpatialReader:*/ true, shouldReleaseConnection);
            return new ObjectResult<TElement>(
                shaper, entitySet, MetadataHelper.GetElementType(columnMap.Type), readerOwned, shouldReleaseConnection);
        }

        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        private DbCommand CreateStoreCommand(string commandText, params object[] parameters)
        {
            var command = ((EntityConnection)Connection).StoreConnection.CreateCommand();
            command.CommandText = commandText;

            // get relevant state from the object context
            if (CommandTimeout.HasValue)
            {
                command.CommandTimeout = CommandTimeout.Value;
            }

            var entityTransaction = ((EntityConnection)Connection).CurrentTransaction;
            if (null != entityTransaction)
            {
                command.Transaction = entityTransaction.StoreTransaction;
            }

            if (null != parameters
                && parameters.Length > 0)
            {
                var dbParameters = new DbParameter[parameters.Length];

                // three cases: all explicit DbParameters, no explicit DbParameters
                // or a mix of the two (throw in the last case)
                if (parameters.All(p => p is DbParameter))
                {
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        dbParameters[i] = (DbParameter)parameters[i];
                    }
                }
                else if (!parameters.Any(p => p is DbParameter))
                {
                    var parameterNames = new string[parameters.Length];
                    var parameterSql = new string[parameters.Length];
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        parameterNames[i] = string.Format(CultureInfo.InvariantCulture, "p{0}", i);
                        dbParameters[i] = command.CreateParameter();
                        dbParameters[i].ParameterName = parameterNames[i];
                        dbParameters[i].Value = parameters[i] ?? DBNull.Value;

                        // By default, we attempt to swap in a SQL Server friendly representation of the parameter.
                        // For other providers, users may write:
                        //
                        //      ExecuteStoreQuery("select * from foo f where f.X = ?", 1);
                        //
                        // rather than:
                        //
                        //      ExecuteStoreQuery("select * from foo f where f.X = {0}", 1);
                        parameterSql[i] = "@" + parameterNames[i];
                    }
                    command.CommandText = string.Format(CultureInfo.InvariantCulture, command.CommandText, parameterSql);
                }
                else
                {
                    throw new InvalidOperationException(Strings.ObjectContext_ExecuteCommandWithMixOfDbParameterAndValues);
                }

                command.Parameters.AddRange(dbParameters);
            }

            return command;
        }

        /// <summary>
        ///     Creates the database using the current store connection and the metadata in the StoreItemCollection. Most of the actual work
        ///     is done by the DbProviderServices implementation for the current store connection.
        /// </summary>
        public virtual void CreateDatabase()
        {
            var storeConnection = ((EntityConnection)Connection).StoreConnection;
            var services = GetStoreItemCollection().StoreProviderFactory.GetProviderServices();
            services.CreateDatabase(storeConnection, CommandTimeout, GetStoreItemCollection());
        }

        /// <summary>
        ///     Deletes the database that is specified as the database in the current store connection. Most of the actual work
        ///     is done by the DbProviderServices implementation for the current store connection.
        /// </summary>
        public virtual void DeleteDatabase()
        {
            var storeConnection = ((EntityConnection)Connection).StoreConnection;
            var services = GetStoreItemCollection().StoreProviderFactory.GetProviderServices();
            services.DeleteDatabase(storeConnection, CommandTimeout, GetStoreItemCollection());
        }

        /// <summary>
        ///     Checks if the database that is specified as the database in the current store connection exists on the store. Most of the actual work
        ///     is done by the DbProviderServices implementation for the current store connection.
        /// </summary>
        public virtual bool DatabaseExists()
        {
            var storeConnection = ((EntityConnection)Connection).StoreConnection;
            var services = GetStoreItemCollection().StoreProviderFactory.GetProviderServices();
            return services.DatabaseExists(storeConnection, CommandTimeout, GetStoreItemCollection());
        }

        private StoreItemCollection GetStoreItemCollection()
        {
            var entityConnection = (EntityConnection)Connection;
            // retrieve the item collection from the entity connection rather than the context since:
            // a) it forces creation of the metadata workspace if it's not already there
            // b) the store item collection isn't guaranteed to exist on the context.MetadataWorkspace
            return (StoreItemCollection)entityConnection.GetMetadataWorkspace().GetItemCollection(DataSpace.SSpace);
        }

        /// <summary>
        ///     Creates the sql script that can be used to create the database for the metadata in the StoreItemCollection. Most of the actual work
        ///     is done by the DbProviderServices implementation for the current store connection.
        /// </summary>
        public virtual String CreateDatabaseScript()
        {
            var services = GetStoreItemCollection().StoreProviderFactory.GetProviderServices();
            var targetProviderManifestToken = GetStoreItemCollection().StoreProviderManifestToken;
            return services.CreateDatabaseScript(targetProviderManifestToken, GetStoreItemCollection());
        }

        #endregion //Methods

        #region Nested types

        /// <summary>
        ///     Supports binding EntityClient parameters to Object Services parameters.
        /// </summary>
        private class ParameterBinder
        {
            private readonly EntityParameter _entityParameter;
            private readonly ObjectParameter _objectParameter;

            internal ParameterBinder(EntityParameter entityParameter, ObjectParameter objectParameter)
            {
                _entityParameter = entityParameter;
                _objectParameter = objectParameter;
            }

            internal void OnDataReaderClosingHandler(object sender, EventArgs args)
            {
                // When the reader is closing, out/inout parameter values are set on the EntityParameter
                // instance. Pass this value through to the corresponding ObjectParameter.
                if (_entityParameter.Value != DBNull.Value
                    && _objectParameter.MappableType.IsEnum)
                {
                    _objectParameter.Value = Enum.ToObject(_objectParameter.MappableType, _entityParameter.Value);
                }
                else
                {
                    _objectParameter.Value = _entityParameter.Value;
                }
            }
        }

        #endregion
    }
}
