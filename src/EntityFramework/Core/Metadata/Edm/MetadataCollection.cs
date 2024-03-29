// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Core.Metadata.Edm
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Data.Entity.Resources;
    using System.Data.Entity.Utilities;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading;

    /// <summary>
    ///     Class representing an actual implementaton of a collection of metadata objects
    /// </summary>
    /// <typeparam name="T"> The type of items in this collection </typeparam>
    internal class MetadataCollection<T> : IList<T>
        where T : MetadataItem
    {
        // The way the collection supports both case sensitive and insensitive search is that it maintains two lists: one list
        // for keep tracking of the order (the ordered list) and another list sorted case sensitively (the sorted list) by the
        // identity of the item.  When a look up on ordinal is requested, the ordered list is used.  When a look up on the name
        // is requested, the sorted list is used.  The two list must be kept in sync for all update operations.  For case
        // sensitive name lookup, the sorted list is searched.  For case insensitive name lookup, a binary search is used on the
        // sorted list to find the match.

        // Note: Care must be taken when modifying logic in this class to call virtual methods in this class.  Since virtual methods
        // can be override by a derived class, the possible results must be thought through.  If needed, add an internal method and
        // have the public virtual method delegates to it.

        /// <summary>
        ///     Default constructor for constructing an empty collection
        /// </summary>
        internal MetadataCollection()
            : this(null)
        {
        }

        /// <summary>
        ///     The constructor for constructing the collection with the given items
        /// </summary>
        /// <param name="items"> The items to populate the collection </param>
        internal MetadataCollection(IEnumerable<T> items)
        {
            _collectionData = new CollectionData();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null)
                    {
                        throw new ArgumentException(Strings.ADP_CollectionParameterElementIsNull("items"));
                    }

                    Debug.Assert(!String.IsNullOrEmpty(item.Identity), "Identity of the item must never be null or empty");
                    AddInternal(item);
                }
            }
        }

        /// <summary>
        ///     structure to contain the indexes of items whose identity match by OrdinalIgnoreCase
        /// </summary>
        private struct OrderedIndex
        {
            /// <summary>
            ///     the index of the item whose identity was used to create the initial dictionary entry
            /// </summary>
            internal readonly int ExactIndex;

            /// <summary>
            ///     the continuation of indexes whose item identities match by OrdinalIgnoreCase
            /// </summary>
            internal readonly int[] InexactIndexes;

            internal OrderedIndex(int exactIndex, int[] inexactIndexes)
            {
                ExactIndex = exactIndex;
                InexactIndexes = inexactIndexes;
            }
        }

        private CollectionData _collectionData;
        private bool _readOnly;

        /// <summary>
        ///     Gets whether the collection is a readonly collection
        /// </summary>
        public bool IsReadOnly
        {
            get { return _readOnly; }
        }

        /// <summary>
        ///     Returns the collection as a readonly collection
        /// </summary>
        public virtual ReadOnlyCollection<T> AsReadOnly
        {
            get { return _collectionData.OrderedList.AsReadOnly(); }
        }

        /// <summary>
        ///     Returns the collection as a read-only metadata collection.
        /// </summary>
        public virtual ReadOnlyMetadataCollection<T> AsReadOnlyMetadataCollection()
        {
            return new ReadOnlyMetadataCollection<T>(this);
        }

        /// <summary>
        ///     Gets the count on the number of items in the collection
        /// </summary>
        public virtual int Count
        {
            get { return _collectionData.OrderedList.Count; }
        }

        /// <summary>
        ///     Gets an item from the collection with the given index
        /// </summary>
        /// <param name="index"> The index to search for </param>
        /// <returns> An item from the collection </returns>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the index is out of the range for the Collection</exception>
        public virtual T this[int index]
        {
            get { return _collectionData.OrderedList[index]; }
            set
            {
                ThrowIfReadOnly();
                Debug.Assert(_collectionData.IdentityDictionary.Value == null);

                _collectionData.OrderedList[index] = value;
            }
        }

        /// <summary>
        ///     Gets an item from the collection with the given identity
        /// </summary>
        /// <param name="identity"> The identity of the item to search for </param>
        /// <returns> An item from the collection </returns>
        /// <exception cref="System.ArgumentNullException">Thrown if identity argument passed in is null</exception>
        /// <exception cref="System.ArgumentException">Thrown if the Collection does not have an item with the given identity</exception>
        /// <exception cref="System.InvalidOperationException">Always thrown on setter</exception>
        public virtual T this[string identity]
        {
            get { return GetValue(identity, false); }
            set { throw new InvalidOperationException(Strings.OperationOnReadOnlyCollection); }
        }

        /// <summary>
        ///     Gets an item from the collection with the given identity
        /// </summary>
        /// <param name="identity"> The identity of the item to search for </param>
        /// <param name="ignoreCase"> Whether case is ignore in the search </param>
        /// <returns> An item from the collection </returns>
        /// <exception cref="System.ArgumentNullException">Thrown if identity argument passed in is null</exception>
        /// <exception cref="System.ArgumentException">Thrown if the Collection does not have an item with the given identity</exception>
        public virtual T GetValue(string identity, bool ignoreCase)
        {
            var item = InternalTryGetValue(identity, ignoreCase);
            if (null == item)
            {
                throw new ArgumentException(Strings.ItemInvalidIdentity(identity), "identity");
            }
            return item;
        }

        /// <summary>
        ///     Adds an item to the collection
        /// </summary>
        /// <param name="item"> The item to add to the list </param>
        /// <exception cref="System.ArgumentNullException">Thrown if item argument is null</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the item passed in or the collection itself instance is in ReadOnly state</exception>
        /// <exception cref="System.ArgumentException">Thrown if the MetadataCollection already contains an item with the same identity</exception>
        /// <exception cref="System.ArgumentException">Thrown if the item passed into Setter has null or String.Empty identity</exception>
        public virtual void Add(T item)
        {
            AddInternal(item);
        }

        internal void Remove(T item)
        {
            DebugCheck.NotNull(item);
            Debug.Assert(Contains(item));
            ThrowIfReadOnly();

            _collectionData.OrderedList.Remove(item);

            InvalidateCache();
        }

        public void InvalidateCache()
        {
            _collectionData.IdentityDictionary 
                = new Lazy<Dictionary<string, OrderedIndex>>(
                () =>
                    {
                        if (_collectionData.OrderedList.Count > UseSortedListCrossover)
                        {
                            // Rebuild the fast by-identity lookup dictionary

                            var identityDictionary
                                = new Dictionary<string, OrderedIndex>(
                                    _collectionData.OrderedList.Count,
                                    StringComparer.OrdinalIgnoreCase);

                            for (var i = 0; i < _collectionData.OrderedList.Count; i++)
                            {
                                AddToDictionary(
                                    identityDictionary,
                                    _collectionData.OrderedList,
                                    _collectionData.OrderedList[i].Identity,
                                    i,
                                    false);
                            }

                            return identityDictionary;
                        }

                        return null;
                    });
        }

        /// <summary>
        ///     Adds an item to the identityDictionary
        /// </summary>
        /// <param name="collectionData"> The collection data to add to </param>
        /// <param name="identity"> The identity to add </param>
        /// <param name="index"> The identity's index in collection </param>
        /// <param name="updateIfFound"> Whether the item should be updated if a matching item is found. </param>
        /// <returns> Index of the added entity, possibly different from the index parameter if updateIfFound is true. </returns>
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly")]
        private static int AddToDictionary(
            Dictionary<string, OrderedIndex> identityDictionary,
            List<T> orderedList,
            string identity,
            int index,
            bool updateIfFound)
        {
            DebugCheck.NotNull(identityDictionary);
            DebugCheck.NotNull(orderedList);
            DebugCheck.NotEmpty(identity);

            int[] inexact = null;
            OrderedIndex orderIndex;
            var exactIndex = index;

            // find the item(s) by OrdinalIgnoreCase
            if (identityDictionary.TryGetValue(identity, out orderIndex))
            {
                // identity was already tracking an item, verify its not a duplicate by exact name
                if (EqualIdentity(orderedList, orderIndex.ExactIndex, identity))
                {
                    // If the item is already here and we are updating, there is no more work to be done.
                    if (updateIfFound)
                    {
                        return orderIndex.ExactIndex;
                    }

                    throw new ArgumentException(Strings.ItemDuplicateIdentity(identity), "item", null);
                }

                if (null != orderIndex.InexactIndexes)
                {
                    // search against the ExactIndex and all InexactIndexes
                    // identity was already tracking multiple items, verify its not a duplicate by exact name
                    for (var i = 0; i < orderIndex.InexactIndexes.Length; ++i)
                    {
                        if (EqualIdentity(orderedList, orderIndex.InexactIndexes[i], identity))
                        {
                            // If the item is already here and we are updating, there is no more work to be done.
                            if (updateIfFound)
                            {
                                return orderIndex.InexactIndexes[i];
                            }

                            throw new ArgumentException(Strings.ItemDuplicateIdentity(identity), "item", null);
                        }
                    }
                    // add another item for existing identity that already was tracking multiple items
                    inexact = new int[orderIndex.InexactIndexes.Length + 1];
                    orderIndex.InexactIndexes.CopyTo(inexact, 0);
                    inexact[inexact.Length - 1] = index;
                }
                else
                {
                    // turn the previously unique identity by ignore case into a multiple item for identity by ignore case
                    inexact = new int[1] { index };
                }
                // the index of the item whose identity was used to create the initial dictionary entry
                exactIndex = orderIndex.ExactIndex;
            }
            // else this is a new identity

            identityDictionary[identity] = new OrderedIndex(exactIndex, inexact);

            return index;
        }

        /// <summary>
        ///     Adds an item to the collection
        /// </summary>
        /// <remarks>
        ///     This method only exists to allow ctor to avoid virtual method call
        /// </remarks>
        /// <param name="item"> The item to add to the list </param>
        /// <exception cref="System.ArgumentNullException">Thrown if item argument is null</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the item passed in or the collection itself instance is in ReadOnly state</exception>
        /// <exception cref="System.ArgumentException">Thrown if the MetadataCollection already contains an item with the same identity</exception>
        /// <exception cref="System.ArgumentException">Thrown if the item passed into Setter has null or String.Empty identity</exception>
        private void AddInternal(T item)
        {
            Util.AssertItemHasIdentity(item, "item");
            ThrowIfReadOnly();

            AddInternalHelper(item, _collectionData, false);
        }

        // This magic number was determined by the performance test cases in SQLBU 489927.
        // It compared Dictionary (hashtable), SortedList (binary search) and linear searching.
        // Its the approximate (x86) point at which doing a OrdinalCaseInsenstive linear search on _orderedItems.
        // becomes slower than doing a OrdinalCaseInsenstive Dictionary lookup in identityDictionary.
        // On x64, the crossover point is lower - but we desire to keep a smaller overal memory footprint.
        // We expect the ItemCollections to be large, but individual Member/Facet collections to be small.
        private const int UseSortedListCrossover = 25;

        /// <summary>
        ///     Adds an item to the collection represented by a list and a dictionary
        /// </summary>
        /// <param name="item"> The item to add to the list </param>
        /// <param name="collectionData"> The collection data where the item will be added </param>
        /// <param name="updateIfFound"> Whether the item should be updated if a matching item is found. </param>
        /// <exception cref="System.ArgumentNullException">Thrown if item argument is null</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the item passed in or the collection itself instance is in ReadOnly state</exception>
        /// <exception cref="System.ArgumentException">Thrown if the MetadataCollection already contains an item with the same identity</exception>
        /// <exception cref="System.ArgumentException">Thrown if the item passed into Setter has null or String.Empty identity</exception>
        /// <remarks>
        ///     If updateIfFound is true, then an update is done in-place instead of
        ///     having an exception thrown. The in-place aspect is required to avoid
        ///     disrupting the indices generated for indexed items, and to enable
        ///     foreach loops to be able to modify the enumerated facets as if it
        ///     were a property update rather than an instance replacement.
        /// </remarks>
        private static void AddInternalHelper(T item, CollectionData collectionData, bool updateIfFound)
        {
            Util.AssertItemHasIdentity(item, "item");

            int index;
            var listCount = collectionData.OrderedList.Count;
            if (null != collectionData.IdentityDictionary.Value)
            {
                index
                    = AddToDictionary(
                        collectionData.IdentityDictionary.Value,
                        collectionData.OrderedList,
                        item.Identity,
                        listCount,
                        updateIfFound);
            }
            else
            {
                // We only have to take care of the ordered list.
                index = IndexOf(collectionData, item.Identity, false);
                if (0 <= index)
                {
                    // The item is found in the linear ordered list. Unless
                    // we're updating, it's an error.
                    if (!updateIfFound)
                    {
                        throw new ArgumentException(Strings.ItemDuplicateIdentity(item.Identity), "item", null);
                    }
                }
                else
                {
                    // This is a new item to be inserted. Grow if we must before adding to ordered list.
                    if (UseSortedListCrossover <= listCount)
                    {
                        collectionData.IdentityDictionary
                            = new Lazy<Dictionary<string, OrderedIndex>>(
                                () =>
                                    {
                                        var identityDictionary
                                            = new Dictionary<string, OrderedIndex>(
                                                collectionData.OrderedList.Count + 1,
                                                StringComparer.OrdinalIgnoreCase);

                                        for (var i = 0; i < collectionData.OrderedList.Count; ++i)
                                        {
                                            AddToDictionary(
                                                identityDictionary,
                                                collectionData.OrderedList,
                                                collectionData.OrderedList[i].Identity,
                                                i,
                                                false);
                                        }

                                        return identityDictionary;
                                    });
                    }
                }
            }

            // Index will be listCount when AddToDictionary doesn't find
            // an existing match, and -1 if IndexOf doesn't find in ordered list.
            if (0 <= index
                && index < listCount)
            {
                collectionData.OrderedList[index] = item;
            }
            else
            {
                Debug.Assert(index == -1 || index == listCount);
                collectionData.OrderedList.Add(item);
            }
        }

        /// <summary>
        ///     Adds a collection of items to the collection
        /// </summary>
        /// <param name="items"> The items to add to the list </param>
        /// <exception cref="System.ArgumentNullException">Thrown if item argument is null</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the item passed in or the collection itself instance is in ReadOnly state</exception>
        /// <exception cref="System.ArgumentException">Thrown if the item that is being added already belongs to another ItemCollection</exception>
        /// <exception cref="System.ArgumentException">Thrown if the ItemCollection already contains an item with the same identity</exception>
        /// <returns> Whether the add was successful </returns>
        internal bool AtomicAddRange(List<T> items)
        {
            var originalData = _collectionData;
            var newData = new CollectionData(originalData, items.Count);

            // Add the new items, this will also perform duplication check
            foreach (var item in items)
            {
                if (item == null)
                {
                    throw new ArgumentException(Strings.ADP_CollectionParameterElementIsNull("items"));
                }
                Debug.Assert(!String.IsNullOrEmpty(item.Identity), "Identity of the item must never be null or empty");

                AddInternalHelper(item, newData, false);
            }

            var swappedOutData = Interlocked.CompareExchange(ref _collectionData, newData, originalData);

            // Check if the exchange was done, if not, then someone must have changed the data in the meantime, so
            // return false
            if (swappedOutData != originalData)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Does Item at index have the same identity
        /// </summary>
        private static bool EqualIdentity(List<T> orderedList, int index, string identity)
        {
            return (orderedList[index].Identity == identity);
        }

        /// <summary>
        ///     Not supported, the collection is treated as read-only.
        /// </summary>
        /// <param name="index"> The index where to insert the given item </param>
        /// <param name="item"> The item to be inserted </param>
        /// <exception cref="System.InvalidOperationException">Thrown if the item passed in or the collection itself instance is in ReadOnly state</exception>
        void IList<T>.Insert(int index, T item)
        {
            throw new InvalidOperationException(Strings.OperationOnReadOnlyCollection);
        }

        /// <summary>
        ///     Not supported, the collection is treated as read-only.
        /// </summary>
        /// <param name="item"> The item to be removed </param>
        /// <returns> True if the item is actually removed, false if the item is not in the list </returns>
        /// <exception cref="System.InvalidOperationException">Always thrown</exception>
        bool ICollection<T>.Remove(T item)
        {
            throw new InvalidOperationException(Strings.OperationOnReadOnlyCollection);
        }

        /// <summary>
        ///     Not supported, the collection is treated as read-only.
        /// </summary>
        /// <param name="index"> The index at which the item is removed </param>
        /// <exception cref="System.InvalidOperationException">Always thrown</exception>
        void IList<T>.RemoveAt(int index)
        {
            throw new InvalidOperationException(Strings.OperationOnReadOnlyCollection);
        }

        /// <summary>
        ///     Not supported, the collection is treated as read-only.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">Always thrown</exception>
        void ICollection<T>.Clear()
        {
            throw new InvalidOperationException(Strings.OperationOnReadOnlyCollection);
        }

        /// <summary>
        ///     Determines if this collection contains the given item
        /// </summary>
        /// <param name="item"> The item to check for </param>
        /// <returns> True if the collection contains the item </returns>
        /// <exception cref="System.ArgumentNullException">Thrown if item argument passed in is null</exception>
        /// <exception cref="System.ArgumentException">Thrown if the item passed in has null or String.Empty identity</exception>
        public bool Contains(T item)
        {
            Util.AssertItemHasIdentity(item, "item");
            return (-1 != IndexOf(item));
        }

        /// <summary>
        ///     Determines if this collection contains an item of the given identity
        /// </summary>
        /// <param name="identity"> The identity of the item to check for </param>
        /// <returns> True if the collection contains the item with the given identity </returns>
        /// <exception cref="System.ArgumentNullException">Thrown if identity argument passed in is null</exception>
        /// <exception cref="System.ArgumentException">Thrown if identity argument passed in is empty string</exception>
        public virtual bool ContainsIdentity(string identity)
        {
            Check.NotEmpty(identity, "identity");
            return (0 <= IndexOf(_collectionData, identity, false));
        }

        /// <summary>
        ///     Find the index of an item identitified by identity
        /// </summary>
        /// <param name="collectionData"> The collection data to search in </param>
        /// <param name="identity"> The identity whose index is to be returned </param>
        /// <param name="ignoreCase"> Should OrdinalIgnoreCase be used? </param>
        /// <returns> The index of the found item, -1 if not found </returns>
        /// <exception cref="System.ArgumentException">Thrown if ignoreCase and an exact match does not exist, but has multiple inexact matches</exception>
        private static int IndexOf(CollectionData collectionData, string identity, bool ignoreCase)
        {
            DebugCheck.NotNull(identity);

            var index = -1;
            if (null != collectionData.IdentityDictionary.Value)
            {
                // OrdinalIgnoreCase dictionary lookup
                OrderedIndex orderIndex;
                if (collectionData.IdentityDictionary.Value.TryGetValue(identity, out orderIndex))
                {
                    if (ignoreCase)
                    {
                        index = orderIndex.ExactIndex;
                    }
                        //return this, only in case when ignore case is false
                    else if (EqualIdentity(collectionData.OrderedList, orderIndex.ExactIndex, identity))
                    {
                        // fast return if exact match
                        return orderIndex.ExactIndex;
                    }

                    // search against the ExactIndex and all InexactIndexes
                    if (null != orderIndex.InexactIndexes)
                    {
                        if (ignoreCase)
                        {
                            // the ignoreCase will throw,
                            throw new InvalidOperationException(Strings.MoreThanOneItemMatchesIdentity(identity));
                        }
                        // search for the exact match or throw if ignoreCase
                        for (var i = 0; i < orderIndex.InexactIndexes.Length; ++i)
                        {
                            if (EqualIdentity(collectionData.OrderedList, orderIndex.InexactIndexes[i], identity))
                            {
                                return orderIndex.InexactIndexes[i];
                            }
                        }
                    }
                    Debug.Assert(ignoreCase == (0 <= index), "indexof verification");
                }
            }
            else if (ignoreCase)
            {
                // OrdinalIgnoreCase linear search
                for (var i = 0; i < collectionData.OrderedList.Count; ++i)
                {
                    if (String.Equals(collectionData.OrderedList[i].Identity, identity, StringComparison.OrdinalIgnoreCase))
                    {
                        if (0 <= index)
                        {
                            throw new InvalidOperationException(Strings.MoreThanOneItemMatchesIdentity(identity));
                        }
                        index = i;
                    }
                }
            }
            else
            {
                // Ordinal linear search
                for (var i = 0; i < collectionData.OrderedList.Count; ++i)
                {
                    if (EqualIdentity(collectionData.OrderedList, i, identity))
                    {
                        return i;
                    }
                }
            }
            return index;
        }

        /// <summary>
        ///     Find the index of an item
        /// </summary>
        /// <param name="item"> The item whose index is to be looked for </param>
        /// <returns> The index of the found item, -1 if not found </returns>
        /// <exception cref="System.ArgumentNullException">Thrown if item argument passed in is null</exception>
        /// <exception cref="System.ArgumentException">Thrown if the item passed in has null or String.Empty identity</exception>
        public virtual int IndexOf(T item)
        {
            Util.AssertItemHasIdentity(item, "item");
            var index = IndexOf(_collectionData, item.Identity, false);

            if (index != -1
                && _collectionData.OrderedList[index] == item)
            {
                return index;
            }

            return -1;
        }

        /// <summary>
        ///     Copies the items in this collection to an array
        /// </summary>
        /// <param name="array"> The array to copy to </param>
        /// <param name="arrayIndex"> The index in the array at which to start the copy </param>
        /// <exception cref="System.ArgumentNullException">Thrown if array argument is null</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the arrayIndex is less than zero</exception>
        /// <exception cref="System.ArgumentException">Thrown if the array argument passed in with respect to the arrayIndex passed in not big enough to hold the MetadataCollection being copied</exception>
        public virtual void CopyTo(T[] array, int arrayIndex)
        {
            Check.NotNull(array, "array");

            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException("arrayIndex");
            }

            if (_collectionData.OrderedList.Count
                > array.Length - arrayIndex)
            {
                throw new ArgumentException(Strings.ArrayTooSmall, "arrayIndex");
            }

            _collectionData.OrderedList.CopyTo(array, arrayIndex);
        }

        /// <summary>
        ///     Gets the enumerator over this collection
        /// </summary>
        /// <returns> </returns>
        public ReadOnlyMetadataCollection<T>.Enumerator GetEnumerator()
        {
            return new ReadOnlyMetadataCollection<T>.Enumerator(this);
        }

        /// <summary>
        ///     Gets the enumerator over this collection
        /// </summary>
        /// <returns> </returns>
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        ///     Gets the enumerator over this collection
        /// </summary>
        /// <returns> </returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        ///     Set this collection as readonly so no more changes can be made on it
        /// </summary>
        public MetadataCollection<T> SetReadOnly()
        {
            for (var i = 0; i < _collectionData.OrderedList.Count; i++)
            {
                _collectionData.OrderedList[i].SetReadOnly();
            }
            _collectionData.OrderedList.TrimExcess();
            _readOnly = true;
            return this;
        }

        /// <summary>
        ///     Gets an item from the collection with the given identity
        /// </summary>
        /// <param name="identity"> The identity of the item to search for </param>
        /// <param name="ignoreCase"> Whether case is ignore in the search </param>
        /// <param name="item"> An item from the collection, null if the item is not found </param>
        /// <returns> True an item is retrieved </returns>
        /// <exception cref="System.ArgumentNullException">Thrown if the identity argument is null</exception>
        public virtual bool TryGetValue(string identity, bool ignoreCase, out T item)
        {
            item = InternalTryGetValue(identity, ignoreCase);
            return (null != item);
        }

        /// <summary>
        ///     Gets an item from the collection with the given identity
        /// </summary>
        /// <param name="identity"> The identity of the item to search for </param>
        /// <param name="ignoreCase"> Whether case is ignore in the search </param>
        /// <returns> item else null </returns>
        private T InternalTryGetValue(string identity, bool ignoreCase)
        {
            var index = IndexOf(_collectionData, Check.NotNull(identity, "identity"), ignoreCase);
            Debug.Assert(
                (index < 0) ||
                (ignoreCase && String.Equals(_collectionData.OrderedList[index].Identity, identity, StringComparison.OrdinalIgnoreCase)) ||
                EqualIdentity(_collectionData.OrderedList, index, identity), "different exact identity");
            return (0 <= index) ? _collectionData.OrderedList[index] : null;
        }

        /// <summary>
        ///     Throws an appropriate exception if the given item is a readonly, used when an attempt is made to change
        ///     the collection
        /// </summary>
        internal void ThrowIfReadOnly()
        {
            if (IsReadOnly)
            {
                throw new InvalidOperationException(Strings.OperationOnReadOnlyCollection);
            }
        }

        /// <summary>
        ///     The data structures for this collection, which contains a list and a dictionary
        /// </summary>
        private class CollectionData
        {
            /// <summary>
            ///     The IdentityDictionary is a case-insensitive dictionary
            ///     used after a certain # of elements have been added to the OrderedList.
            ///     It aids in fast lookup by T.Identity by mapping a string value to
            ///     an OrderedIndex structure with other case-insensitive matches for the
            ///     entry.  See additional comments in AddInternal.
            /// </summary>
            internal Lazy<Dictionary<string, OrderedIndex>> IdentityDictionary
                = new Lazy<Dictionary<string, OrderedIndex>>(() => null);

            internal readonly List<T> OrderedList;

            internal CollectionData()
            {
                OrderedList = new List<T>();
            }

            internal CollectionData(CollectionData original, int additionalCapacity)
            {
                OrderedList = new List<T>(original.OrderedList.Count + additionalCapacity);
                foreach (var item in original.OrderedList)
                {
                    // using AddRange results in a temporary memory allocation
                    OrderedList.Add(item);
                }

                if (UseSortedListCrossover <= OrderedList.Capacity)
                {
                    IdentityDictionary
                        = new Lazy<Dictionary<string, OrderedIndex>>(
                            () =>
                                {
                                    var identityDictionary
                                        = new Dictionary<string, OrderedIndex>(
                                            OrderedList.Capacity,
                                            StringComparer.OrdinalIgnoreCase);

                                    if (null != original.IdentityDictionary.Value)
                                    {
                                        foreach (var pair in original.IdentityDictionary.Value)
                                        {
                                            identityDictionary.Add(pair.Key, pair.Value);
                                        }
                                    }
                                    else
                                    {
                                        for (var i = 0; i < OrderedList.Count; ++i)
                                        {
                                            AddToDictionary(identityDictionary, OrderedList, OrderedList[i].Identity, i, false);
                                        }
                                    }

                                    return identityDictionary;
                                });
                }
            }
        }
    }
}
