// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Core.Metadata.Edm
{
    using System.Collections.Generic;
    using System.Data.Entity.Utilities;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;

    /// <summary>
    ///     concrete Representation the Entity Type
    /// </summary>
    [SuppressMessage("Microsoft.Maintainability", "CA1501:AvoidExcessiveInheritance")]
    public class EntityType : EntityTypeBase
    {
        private ReadOnlyMetadataCollection<EdmProperty> _properties;

        /// <summary>
        ///     Initializes a new instance of Entity Type
        /// </summary>
        /// <param name="name"> name of the entity type </param>
        /// <param name="namespaceName"> namespace of the entity type </param>
        /// <param name="dataSpace"> dataspace in which the EntityType belongs to </param>
        /// <exception cref="System.ArgumentNullException">Thrown if either name, namespace or version arguments are null</exception>
        internal EntityType(string name, string namespaceName, DataSpace dataSpace)
            : base(name, namespaceName, dataSpace)
        {
        }

        /// <param name="name"> name of the entity type </param>
        /// <param name="namespaceName"> namespace of the entity type </param>
        /// <param name="dataSpace"> dataspace in which the EntityType belongs to </param>
        /// <param name="members"> members of the entity type [property and navigational property] </param>
        /// <param name="keyMemberNames"> key members for the type </param>
        /// <exception cref="System.ArgumentNullException">Thrown if either name, namespace or version arguments are null</exception>
        internal EntityType(
            string name,
            string namespaceName,
            DataSpace dataSpace,
            IEnumerable<string> keyMemberNames,
            IEnumerable<EdmMember> members)
            : base(name, namespaceName, dataSpace)
        {
            //--- first add the properties 
            if (null != members)
            {
                CheckAndAddMembers(members, this);
            }
            //--- second add the key members
            if (null != keyMemberNames)
            {
                //Validation should make sure that base type of this type does not have keymembers when this type has keymembers. 
                CheckAndAddKeyMembers(keyMemberNames);
            }
        }

        /// <summary>
        ///     cached dynamic method to construct a CLR instance
        /// </summary>
        private RefType _referenceType;

        private RowType _keyRow;

        private readonly List<ForeignKeyBuilder> _foreignKeyBuilders = new List<ForeignKeyBuilder>();

        internal IEnumerable<ForeignKeyBuilder> ForeignKeyBuilders
        {
            get { return _foreignKeyBuilders; }
        }

        internal void RemoveForeignKey(ForeignKeyBuilder foreignKeyBuilder)
        {
            DebugCheck.NotNull(foreignKeyBuilder);
            Util.ThrowIfReadOnly(this);

            foreignKeyBuilder.SetOwner(null);

            _foreignKeyBuilders.Remove(foreignKeyBuilder);
        }

        internal void AddForeignKey(ForeignKeyBuilder foreignKeyBuilder)
        {
            DebugCheck.NotNull(foreignKeyBuilder);
            Util.ThrowIfReadOnly(this);

            foreignKeyBuilder.SetOwner(this);

            _foreignKeyBuilders.Add(foreignKeyBuilder);
        }

        public ReadOnlyMetadataCollection<EdmProperty> DeclaredKeyProperties
        {
            get
            {
                return new ReadOnlyMetadataCollection<EdmProperty>(
                    KeyMembers.Where(km => DeclaredMembers.Contains(km)).Cast<EdmProperty>().ToList());
            }
        }

        /// <summary>
        ///     Returns the kind of the type
        /// </summary>
        public override BuiltInTypeKind BuiltInTypeKind
        {
            get { return BuiltInTypeKind.EntityType; }
        }

        /// <summary>
        ///     Validates a EdmMember object to determine if it can be added to this type's
        ///     Members collection. If this method returns without throwing, it is assumed
        ///     the member is valid.
        /// </summary>
        /// <param name="member"> The member to validate </param>
        /// <exception cref="System.ArgumentException">Thrown if the member is not a EdmProperty</exception>
        internal override void ValidateMemberForAdd(EdmMember member)
        {
            Debug.Assert(
                Helper.IsEdmProperty(member) || Helper.IsNavigationProperty(member),
                "Only members of type Property may be added to Entity types.");
        }

        public ReadOnlyMetadataCollection<NavigationProperty> DeclaredNavigationProperties
        {
            get { return GetDeclaredOnlyMembers<NavigationProperty>(); }
        }

        /// <summary>
        ///     Returns the list of Navigation Properties for this entity type
        /// </summary>
        public ReadOnlyMetadataCollection<NavigationProperty> NavigationProperties
        {
            get
            {
                return new FilteredReadOnlyMetadataCollection<NavigationProperty, EdmMember>(
                    Members, Helper.IsNavigationProperty);
            }
        }

        public ReadOnlyMetadataCollection<EdmProperty> DeclaredProperties
        {
            get { return GetDeclaredOnlyMembers<EdmProperty>(); }
        }

        public ReadOnlyMetadataCollection<EdmMember> DeclaredMembers
        {
            get { return GetDeclaredOnlyMembers<EdmMember>(); }
        }

        /// <summary>
        ///     Returns just the properties from the collection
        ///     of members on this type
        /// </summary>
        public virtual ReadOnlyMetadataCollection<EdmProperty> Properties
        {
            get
            {
                if (!IsReadOnly)
                {
                    return new FilteredReadOnlyMetadataCollection<EdmProperty, EdmMember>(Members, Helper.IsEdmProperty);
                }

                if (_properties == null)
                {
                    Interlocked.CompareExchange(
                        ref _properties,
                        new FilteredReadOnlyMetadataCollection<EdmProperty, EdmMember>(
                            Members, Helper.IsEdmProperty), null);
                }

                return _properties;
            }
        }

        /// <summary>
        ///     Returns the Reference type pointing to this entity type
        /// </summary>
        /// <returns> </returns>
        [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        public RefType GetReferenceType()
        {
            if (_referenceType == null)
            {
                Interlocked.CompareExchange(ref _referenceType, new RefType(this), null);
            }
            return _referenceType;
        }

        internal RowType GetKeyRowType()
        {
            if (_keyRow == null)
            {
                var keyProperties = new List<EdmProperty>(KeyMembers.Count);
                keyProperties.AddRange(KeyMembers.Select(keyMember => new EdmProperty(keyMember.Name, Helper.GetModelTypeUsage(keyMember))));
                Interlocked.CompareExchange(ref _keyRow, new RowType(keyProperties), null);
            }
            return _keyRow;
        }

        /// <summary>
        ///     Attempts to get the property name for the assoication between the two given end
        ///     names.  Note that this property may not exist if a navigation property is defined
        ///     in one direction but not in the other.
        /// </summary>
        /// <param name="relationshipType"> the relationship for which a nav property is required </param>
        /// <param name="fromName"> the 'from' end of the association </param>
        /// <param name="toName"> the 'to' end of the association </param>
        /// <param name="navigationProperty"> the property name, or null if none was found </param>
        /// <returns> true if a property was found, false otherwise </returns>
        internal bool TryGetNavigationProperty(
            string relationshipType, string fromName, string toName, out NavigationProperty navigationProperty)
        {
            // This is a linear search but it's probably okay because the number of entries
            // is generally small and this method is only called to generate code during lighweight
            // code gen.
            foreach (var navProperty in NavigationProperties)
            {
                if (navProperty.RelationshipType.FullName == relationshipType
                    &&
                    navProperty.FromEndMember.Name == fromName
                    &&
                    navProperty.ToEndMember.Name == toName)
                {
                    navigationProperty = navProperty;
                    return true;
                }
            }
            navigationProperty = null;
            return false;
        }

        /// <summary>
        /// The factory method for constructing the EntityType object.
        /// </summary>
        /// <param name="name">The name of the entity type.</param>
        /// <param name="namespaceName">The namespace of the entity type.</param>
        /// <param name="dataSpace">The dataspace in which the EntityType belongs to.</param>
        /// <param name="members">Members of the entity type (primitive and navigation properties).</param>
        /// <param name="keyMemberNames">Name of key members for the type.</param>
        /// <exception cref="System.ArgumentException">Thrown if either name, namespace arguments are null.</exception>
        /// <notes>The newly created EntityType will be read only.</notes>
        public static EntityType Create(
            string name,
            string namespaceName,
            DataSpace dataSpace,
            IEnumerable<string> keyMemberNames,
            IEnumerable<EdmMember> members,
            IEnumerable<MetadataProperty> metadataProperties)
        {
            Check.NotNull(name, "name");
            Check.NotNull(namespaceName, "namespaceName");

            var entity = new EntityType(name, namespaceName, dataSpace, keyMemberNames, members);

            if (metadataProperties != null)
            {
                entity.AddMetadataProperties(metadataProperties.ToList());
            }

            entity.SetReadOnly();
            return entity;
        }
    }
}
