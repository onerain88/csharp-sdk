﻿using LeanCloud.Storage.Internal;
using LeanCloud.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;

namespace LeanCloud {
    /// <summary>
    /// AVObject
    /// </summary>
    public class AVObject : IEnumerable<KeyValuePair<string, object>>, INotifyPropertyChanged, INotifyPropertyUpdated, INotifyCollectionPropertyUpdated {
        private static readonly string AutoClassName = "_Automatic";

#if UNITY
        private static readonly bool isCompiledByIL2CPP = AppDomain.CurrentDomain.FriendlyName.Equals("IL2CPP Root Domain");
#else
        private static readonly bool isCompiledByIL2CPP = false;
#endif


        internal readonly object mutex = new object();

        private readonly LinkedList<IDictionary<string, IAVFieldOperation>> operationSetQueue =
            new LinkedList<IDictionary<string, IAVFieldOperation>>();
        private readonly IDictionary<string, object> estimatedData = new Dictionary<string, object>();

        private static readonly ThreadLocal<bool> isCreatingPointer = new ThreadLocal<bool>(() => false);

        private bool hasBeenFetched;
        private bool dirty;
        internal TaskQueue taskQueue = new TaskQueue();

        private IObjectState state;
        internal void MutateState(Action<MutableObjectState> func) {
            lock (mutex) {
                state = state.MutatedClone(func);

                // Refresh the estimated data.
                RebuildEstimatedData();
            }
        }

        public IObjectState State {
            get {
                return state;
            }
        }

        internal static AVObjectController ObjectController {
            get {
                return AVPlugins.Instance.ObjectController;
            }
        }

        internal static ObjectSubclassingController SubclassingController {
            get {
                return AVPlugins.Instance.SubclassingController;
            }
        }

        public static string GetSubClassName<TAVObject>() {
            return SubclassingController.GetClassName(typeof(TAVObject));
        }

        #region AVObject Creation

        /// <summary>
        /// Constructor for use in AVObject subclasses. Subclasses must specify a AVClassName attribute.
        /// </summary>
        protected AVObject()
          : this(AutoClassName) {
        }

        /// <summary>
        /// Constructs a new AVObject with no data in it. A AVObject constructed in this way will
        /// not have an ObjectId and will not persist to the database until <see cref="SaveAsync()"/>
        /// is called.
        /// </summary>
        /// <remarks>
        /// Class names must be alphanumerical plus underscore, and start with a letter. It is recommended
        /// to name classes in CamelCaseLikeThis.
        /// </remarks>
        /// <param name="className">The className for this AVObject.</param>
        public AVObject(string className) {
            // We use a ThreadLocal rather than passing a parameter so that createWithoutData can do the
            // right thing with subclasses. It's ugly and terrible, but it does provide the development
            // experience we generally want, so... yeah. Sorry to whomever has to deal with this in the
            // future. I pinky-swear we won't make a habit of this -- you believe me, don't you?
            var isPointer = isCreatingPointer.Value;
            isCreatingPointer.Value = false;

            if (className == null) {
                throw new ArgumentException("You must specify a LeanCloud class name when creating a new AVObject.");
            }
            if (AutoClassName.Equals(className)) {
                className = SubclassingController.GetClassName(GetType());
            }
            // If this is supposed to be created by a factory but wasn't, throw an exception
            if (!SubclassingController.IsTypeValid(className, GetType())) {
                throw new ArgumentException(
                  "You must create this type of AVObject using AVObject.Create() or the proper subclass.");
            }
            state = new MutableObjectState {
                ClassName = className
            };
            OnPropertyChanged("ClassName");

            operationSetQueue.AddLast(new Dictionary<string, IAVFieldOperation>());
            if (!isPointer) {
                hasBeenFetched = true;
                IsDirty = true;
                SetDefaultValues();
            } else {
                IsDirty = false;
                hasBeenFetched = false;
            }
        }

        /// <summary>
        /// Creates a new AVObject based upon a class name. If the class name is a special type (e.g.
        /// for <see cref="AVUser"/>), then the appropriate type of AVObject is returned.
        /// </summary>
        /// <param name="className">The class of object to create.</param>
        /// <returns>A new AVObject for the given class name.</returns>
        public static AVObject Create(string className) {
            return SubclassingController.Instantiate(className);
        }

        /// <summary>
        /// Creates a reference to an existing AVObject for use in creating associations between
        /// AVObjects. Calling <see cref="AVObject.IsDataAvailable"/> on this object will return
        /// <c>false</c> until <see cref="AVExtensions.FetchIfNeededAsync{T}(T)"/> has been called.
        /// No network request will be made.
        /// </summary>
        /// <param name="className">The object's class.</param>
        /// <param name="objectId">The object id for the referenced object.</param>
        /// <returns>A AVObject without data.</returns>
        public static AVObject CreateWithoutData(string className, string objectId) {
            isCreatingPointer.Value = true;
            try {
                var result = SubclassingController.Instantiate(className);
                result.ObjectId = objectId;
                result.IsDirty = false;
                if (result.IsDirty) {
                    throw new InvalidOperationException(
                      "A AVObject subclass default constructor must not make changes to the object that cause it to be dirty.");
                }
                return result;
            } finally {
                isCreatingPointer.Value = false;
            }
        }

        /// <summary>
        /// Creates a new AVObject based upon a given subclass type.
        /// </summary>
        /// <returns>A new AVObject for the given class name.</returns>
        public static T Create<T>() where T : AVObject {
            return (T)SubclassingController.Instantiate(SubclassingController.GetClassName(typeof(T)));
        }

        /// <summary>
        /// Creates a reference to an existing AVObject for use in creating associations between
        /// AVObjects. Calling <see cref="AVObject.IsDataAvailable"/> on this object will return
        /// <c>false</c> until <see cref="AVExtensions.FetchIfNeededAsync{T}(T)"/> has been called.
        /// No network request will be made.
        /// </summary>
        /// <param name="objectId">The object id for the referenced object.</param>
        /// <returns>A AVObject without data.</returns>
        public static T CreateWithoutData<T>(string objectId) where T : AVObject {
            return (T)CreateWithoutData(SubclassingController.GetClassName(typeof(T)), objectId);
        }

        ///<summary>
        /// restore a AVObject of subclass instance from IObjectState.
        /// </summary>
        /// <param name="state">IObjectState after encode from Dictionary.</param>
        /// <param name="defaultClassName">The name of the subclass.</param>
        public static T FromState<T>(IObjectState state, string defaultClassName) where T : AVObject {
            string className = state.ClassName ?? defaultClassName;

            T obj = (T)CreateWithoutData(className, state.ObjectId);
            obj.HandleFetchResult(state);

            return obj;
        }

        #endregion

        public static IDictionary<string, string> GetPropertyMappings(string className) {
            return SubclassingController.GetPropertyMappings(className);
        }

        private static string GetFieldForPropertyName(string className, string propertyName) {
            SubclassingController.GetPropertyMappings(className).TryGetValue(propertyName, out string fieldName);
            return fieldName;
        }

        /// <summary>
        /// Sets the value of a property based upon its associated AVFieldName attribute.
        /// </summary>
        /// <param name="value">The new value.</param>
        /// <param name="propertyName">The name of the property.</param>
        /// <typeparam name="T">The type for the property.</typeparam>
        protected virtual void SetProperty<T>(T value,
#if !UNITY
 [CallerMemberName] string propertyName = null
#else
 string propertyName
#endif
) {
            this[GetFieldForPropertyName(ClassName, propertyName)] = value;
        }

        /// <summary>
        /// Gets a relation for a property based upon its associated AVFieldName attribute.
        /// </summary>
        /// <returns>The AVRelation for the property.</returns>
        /// <param name="propertyName">The name of the property.</param>
        /// <typeparam name="T">The AVObject subclass type of the AVRelation.</typeparam>
        protected AVRelation<T> GetRelationProperty<T>(
#if !UNITY
[CallerMemberName] string propertyName = null
#else
string propertyName
#endif
) where T : AVObject {
            return GetRelation<T>(GetFieldForPropertyName(ClassName, propertyName));
        }

        /// <summary>
        /// Gets the value of a property based upon its associated AVFieldName attribute.
        /// </summary>
        /// <returns>The value of the property.</returns>
        /// <param name="propertyName">The name of the property.</param>
        /// <typeparam name="T">The return type of the property.</typeparam>
        protected virtual T GetProperty<T>(
#if !UNITY
[CallerMemberName] string propertyName = null
#else
string propertyName
#endif
) {
            return GetProperty<T>(default, propertyName);
        }

        /// <summary>
        /// Gets the value of a property based upon its associated AVFieldName attribute.
        /// </summary>
        /// <returns>The value of the property.</returns>
        /// <param name="defaultValue">The value to return if the property is not present on the AVObject.</param>
        /// <param name="propertyName">The name of the property.</param>
        /// <typeparam name="T">The return type of the property.</typeparam>
        protected virtual T GetProperty<T>(T defaultValue,
#if !UNITY
 [CallerMemberName] string propertyName = null
#else
 string propertyName
#endif
        ) {
            if (TryGetValue(GetFieldForPropertyName(ClassName, propertyName), out T result)) {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// Allows subclasses to set values for non-pointer construction.
        /// </summary>
        internal virtual void SetDefaultValues() {
        }

        /// <summary>
        /// Registers a custom subclass type with the LeanCloud SDK, enabling strong-typing of those AVObjects whenever
        /// they appear. Subclasses must specify the AVClassName attribute, have a default constructor, and properties
        /// backed by AVObject fields should have AVFieldName attributes supplied.
        /// </summary>
        /// <typeparam name="T">The AVObject subclass type to register.</typeparam>
        public static void RegisterSubclass<T>() where T : AVObject, new() {
            SubclassingController.RegisterSubclass(typeof(T));
        }

        internal static void UnregisterSubclass<T>() where T : AVObject, new() {
            SubclassingController.UnregisterSubclass(typeof(T));
        }

        /// <summary>
        /// Clears any changes to this object made since the last call to <see cref="SaveAsync()"/>.
        /// </summary>
        public void Revert() {
            lock (mutex) {
                bool wasDirty = CurrentOperations.Count > 0;
                if (wasDirty) {
                    CurrentOperations.Clear();
                    RebuildEstimatedData();
                    OnPropertyChanged("IsDirty");
                }
            }
        }

        internal virtual void HandleFetchResult(IObjectState serverState) {
            lock (mutex) {
                MergeFromServer(serverState);
            }
        }

        internal void HandleFailedSave(
            IDictionary<string, IAVFieldOperation> operationsBeforeSave) {
            lock (mutex) {
                var opNode = operationSetQueue.Find(operationsBeforeSave);
                var nextOperations = opNode.Next.Value;
                bool wasDirty = nextOperations.Count > 0;
                operationSetQueue.Remove(opNode);
                // Merge the data from the failed save into the next save.
                foreach (var pair in operationsBeforeSave) {
                    var operation1 = pair.Value;
                    IAVFieldOperation operation2 = null;
                    nextOperations.TryGetValue(pair.Key, out operation2);
                    if (operation2 != null) {
                        operation2 = operation2.MergeWithPrevious(operation1);
                    } else {
                        operation2 = operation1;
                    }
                    nextOperations[pair.Key] = operation2;
                }
                if (!wasDirty && nextOperations == CurrentOperations && operationsBeforeSave.Count > 0) {
                    OnPropertyChanged("IsDirty");
                }
            }
        }

        internal virtual void HandleSave(IObjectState serverState) {
            lock (mutex) {
                var operationsBeforeSave = operationSetQueue.First.Value;
                operationSetQueue.RemoveFirst();

                // Merge the data from the save and the data from the server into serverData.
                //MutateState(mutableClone =>
                //{
                //    mutableClone.Apply(operationsBeforeSave);
                //});
                state = state.MutatedClone((objectState) => objectState.Apply(operationsBeforeSave));
                MergeFromServer(serverState);
            }
        }

        public virtual void MergeFromServer(IObjectState serverState) {
            // Make a new serverData with fetched values.
            var newServerData = serverState.ToDictionary(t => t.Key, t => t.Value);

            lock (mutex) {
                // Trigger handler based on serverState
                if (serverState.ObjectId != null) {
                    // If the objectId is being merged in, consider this object to be fetched.
                    hasBeenFetched = true;
                    OnPropertyChanged("IsDataAvailable");
                }

                if (serverState.UpdatedAt != null) {
                    OnPropertyChanged("UpdatedAt");
                }

                if (serverState.CreatedAt != null) {
                    OnPropertyChanged("CreatedAt");
                }

                // We cache the fetched object because subsequent Save operation might flush
                // the fetched objects into Pointers.
                IDictionary<string, AVObject> fetchedObject = CollectFetchedObjects();

                foreach (var pair in serverState) {
                    var value = pair.Value;
                    if (value is AVObject) {
                        // Resolve fetched object.
                        var avObject = value as AVObject;
                        if (fetchedObject.ContainsKey(avObject.ObjectId)) {
                            value = fetchedObject[avObject.ObjectId];
                        }
                    }
                    newServerData[pair.Key] = value;
                }

                IsDirty = false;
                serverState = serverState.MutatedClone(mutableClone => {
                    mutableClone.ServerData = newServerData;
                });
                MutateState(mutableClone => {
                    mutableClone.Apply(serverState);
                });
            }
        }

        internal void MergeFromObject(AVObject other) {
            lock (mutex) {
                // If they point to the same instance, we don't need to merge
                if (this == other) {
                    return;
                }
            }

            // Clear out any changes on this object.
            if (operationSetQueue.Count != 1) {
                throw new InvalidOperationException("Attempt to MergeFromObject during save.");
            }
            operationSetQueue.Clear();
            foreach (var operationSet in other.operationSetQueue) {
                operationSetQueue.AddLast(operationSet.ToDictionary(entry => entry.Key,
                                                                    entry => entry.Value));
            }

            lock (mutex) {
                state = other.State;
            }
            RebuildEstimatedData();
        }

        private bool HasDirtyChildren {
            get {
                lock (mutex) {
                    return FindUnsavedChildren().FirstOrDefault() != null;
                }
            }
        }

        /// <summary>
        /// Flattens dictionaries and lists into a single enumerable of all contained objects
        /// that can then be queried over.
        /// </summary>
        /// <param name="root">The root of the traversal</param>
        /// <param name="traverseAVObjects">Whether to traverse into AVObjects' children</param>
        /// <param name="yieldRoot">Whether to include the root in the result</param>
        /// <returns></returns>
        internal static IEnumerable<object> DeepTraversal(
            object root, bool traverseAVObjects = false, bool yieldRoot = false) {
            var items = DeepTraversalInternal(root,
                traverseAVObjects,
                new HashSet<object>(new IdentityEqualityComparer<object>()));
            if (yieldRoot) {
                return new[] { root }.Concat(items);
            } else {
                return items;
            }
        }

        private static IEnumerable<object> DeepTraversalInternal(
            object root, bool traverseAVObjects, ICollection<object> seen) {
            seen.Add(root);
            var itemsToVisit = isCompiledByIL2CPP ? (System.Collections.IEnumerable)null : (IEnumerable<object>)null;
            var dict = Conversion.As<IDictionary<string, object>>(root);
            if (dict != null) {
                itemsToVisit = dict.Values;
            } else {
                var list = Conversion.As<IList<object>>(root);
                if (list != null) {
                    itemsToVisit = list;
                } else if (traverseAVObjects) {
                    var obj = root as AVObject;
                    if (obj != null) {
                        itemsToVisit = obj.Keys.ToList().Select(k => obj[k]);
                    }
                }
            }
            if (itemsToVisit != null) {
                foreach (var i in itemsToVisit) {
                    if (!seen.Contains(i)) {
                        yield return i;
                        var children = DeepTraversalInternal(i, traverseAVObjects, seen);
                        foreach (var child in children) {
                            yield return child;
                        }
                    }
                }
            }
        }

        private IEnumerable<AVObject> FindUnsavedChildren() {
            return DeepTraversal(estimatedData)
                .OfType<AVObject>()
                .Where(o => o.IsDirty);
        }

        /// <summary>
        /// Deep traversal of this object to grab a copy of any object referenced by this object.
        /// These instances may have already been fetched, and we don't want to lose their data when
        /// refreshing or saving.
        /// </summary>
        /// <returns>Map of objectId to AVObject which have been fetched.</returns>
        private IDictionary<string, AVObject> CollectFetchedObjects() {
            return DeepTraversal(estimatedData)
               .OfType<AVObject>()
               .Where(o => o.ObjectId != null && o.IsDataAvailable)
               .GroupBy(o => o.ObjectId)
               .ToDictionary(group => group.Key, group => group.Last());
        }

        public static IDictionary<string, object> ToJSONObjectForSaving(
            IDictionary<string, IAVFieldOperation> operations) {
            var result = new Dictionary<string, object>();
            foreach (var pair in operations) {
                // AVRPCSerialize the data
                var operation = pair.Value;

                result[pair.Key] = PointerOrLocalIdEncoder.Instance.Encode(operation);
            }
            return result;
        }

        internal IDictionary<string, object> EncodeForSaving(IDictionary<string, object> data) {
            var result = new Dictionary<string, object>();
            lock (this.mutex) {
                foreach (var key in data.Keys) {
                    var value = data[key];
                    result.Add(key, PointerOrLocalIdEncoder.Instance.Encode(value));
                }
            }

            return result;
        }


        internal IDictionary<string, object> ServerDataToJSONObjectForSerialization() {
            return PointerOrLocalIdEncoder.Instance.Encode(state.ToDictionary(t => t.Key, t => t.Value))
                as IDictionary<string, object>;
        }

        #region Save Object(s)

        /// <summary>
        /// Pushes new operations onto the queue and returns the current set of operations.
        /// </summary>
        public IDictionary<string, IAVFieldOperation> StartSave() {
            lock (mutex) {
                var currentOperations = CurrentOperations;
                operationSetQueue.AddLast(new Dictionary<string, IAVFieldOperation>());
                OnPropertyChanged("IsDirty");
                return currentOperations;
            }
        }

        public virtual Task SaveAsync(bool fetchWhenSave = false, AVQuery<AVObject> query = null, CancellationToken cancellationToken = default) {
            IDictionary<string, IAVFieldOperation> currentOperations = null;
            if (!IsDirty) {
                return Task.FromResult(0);
            }
            
            Task deepSaveTask;
            lock (mutex) {
                // Get the JSON representation of the object.
                currentOperations = StartSave();
                deepSaveTask = DeepSaveAsync(estimatedData, cancellationToken);
            }

            return deepSaveTask.OnSuccess(_ => {
                return ObjectController.SaveAsync(state,
                    currentOperations,
                    FetchWhenSave || fetchWhenSave,
                    query,
                    cancellationToken);
            }).Unwrap().ContinueWith(t => {
                if (t.IsFaulted || t.IsCanceled) {
                    HandleFailedSave(currentOperations);
                } else {
                    var serverState = t.Result;
                    HandleSave(serverState);
                }
                return t;
            }).Unwrap();
        }

        internal virtual Task<AVObject> FetchAsyncInternal(
              Task toAwait,
              IDictionary<string, object> queryString,
              CancellationToken cancellationToken) {
            return toAwait.OnSuccess(_ => {
                if (ObjectId == null) {
                    throw new InvalidOperationException("Cannot refresh an object that hasn't been saved to the server.");
                }
                if (queryString == null) {
                    queryString = new Dictionary<string, object>();
                }

                return ObjectController.FetchAsync(state, queryString, cancellationToken);
            }).Unwrap().OnSuccess(t => {
                HandleFetchResult(t.Result);
                return this;
            });
        }

        private static Task DeepSaveAsync(object obj, CancellationToken cancellationToken) {
            var objects = new List<AVObject>();
            CollectDirtyChildren(obj, objects);

            var uniqueObjects = new HashSet<AVObject>(objects,
                new IdentityEqualityComparer<AVObject>());

            var saveDirtyFileTasks = DeepTraversal(obj, true)
                .OfType<AVFile>()
                .Where(f => f.IsDirty)
                .Select(f => f.SaveAsync(cancellationToken: cancellationToken)).ToList();

            return Task.WhenAll(saveDirtyFileTasks).OnSuccess(_ => {
                IEnumerable<AVObject> remaining = new List<AVObject>(uniqueObjects);
                return InternalExtensions.WhileAsync(() => Task.FromResult(remaining.Any()), () => {
                    // Partition the objects into two sets: those that can be saved immediately,
                    // and those that rely on other objects to be created first.
                    var current = (from item in remaining
                                   where item.CanBeSerialized
                                   select item).ToList();
                    var nextBatch = (from item in remaining
                                     where !item.CanBeSerialized
                                     select item).ToList();
                    remaining = nextBatch;

                    if (current.Count == 0) {
                        // We do cycle-detection when building the list of objects passed to this
                        // function, so this should never get called. But we should check for it
                        // anyway, so that we get an exception instead of an infinite loop.
                        throw new InvalidOperationException(
                          "Unable to save a AVObject with a relation to a cycle.");
                    }

                    // Save all of the objects in current.
                    return AVObject.EnqueueForAll<object>(current, toAwait => {
                        return toAwait.OnSuccess(__ => {
                            var states = (from item in current
                                          select item.state).ToList();
                            var operationsList = (from item in current
                                                  select item.StartSave()).ToList();

                            var saveTasks = ObjectController.SaveAllAsync(states,
                                operationsList,
                                cancellationToken);

                            return Task.WhenAll(saveTasks).ContinueWith(t => {
                                if (t.IsFaulted || t.IsCanceled) {
                                    foreach (var pair in current.Zip(operationsList, (item, ops) => new { item, ops })) {
                                        pair.item.HandleFailedSave(pair.ops);
                                    }
                                } else {
                                    var serverStates = t.Result;
                                    foreach (var pair in current.Zip(serverStates, (item, state) => new { item, state })) {
                                        pair.item.HandleSave(pair.state);
                                    }
                                }
                                cancellationToken.ThrowIfCancellationRequested();
                                return t;
                            }).Unwrap();
                        }).Unwrap().OnSuccess(t => (object)null);
                    }, cancellationToken);
                });
            }).Unwrap();
        }

        /// <summary>
        /// Saves each object in the provided list.
        /// </summary>
        /// <param name="objects">The objects to save.</param>
        public static Task SaveAllAsync<T>(IEnumerable<T> objects) where T : AVObject {
            return SaveAllAsync(objects, CancellationToken.None);
        }

        /// <summary>
        /// Saves each object in the provided list.
        /// </summary>
        /// <param name="objects">The objects to save.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public static Task SaveAllAsync<T>(
            IEnumerable<T> objects, CancellationToken cancellationToken) where T : AVObject {
            return DeepSaveAsync(objects.ToList(), cancellationToken);
        }

        #endregion

        #region Fetch Object(s)

        /// <summary>
        /// Fetches this object with the data from the server.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal Task<AVObject> FetchAsyncInternal(CancellationToken cancellationToken) {
            return FetchAsyncInternal(null, cancellationToken);
        }

        internal Task<AVObject> FetchAsyncInternal(IDictionary<string, object> queryString, CancellationToken cancellationToken) {
            return taskQueue.Enqueue(toAwait => FetchAsyncInternal(toAwait, queryString, cancellationToken),
               cancellationToken);
        }

        internal Task<AVObject> FetchIfNeededAsyncInternal(
            Task toAwait, CancellationToken cancellationToken) {
            if (!IsDataAvailable) {
                return FetchAsyncInternal(toAwait, null, cancellationToken);
            }
            return Task.FromResult(this);
        }

        /// <summary>
        /// If this AVObject has not been fetched (i.e. <see cref="IsDataAvailable"/> returns
        /// false), fetches this object with the data from the server.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal Task<AVObject> FetchIfNeededAsyncInternal(CancellationToken cancellationToken) {
            return taskQueue.Enqueue(toAwait => FetchIfNeededAsyncInternal(toAwait, cancellationToken),
              cancellationToken);
        }

        /// <summary>
        /// Fetches all of the objects that don't have data in the provided list.
        /// </summary>
        /// <returns>The list passed in for convenience.</returns>
        public static Task<IEnumerable<T>> FetchAllIfNeededAsync<T>(
            IEnumerable<T> objects) where T : AVObject {
            return FetchAllIfNeededAsync(objects, CancellationToken.None);
        }

        /// <summary>
        /// Fetches all of the objects that don't have data in the provided list.
        /// </summary>
        /// <param name="objects">The objects to fetch.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The list passed in for convenience.</returns>
        public static Task<IEnumerable<T>> FetchAllIfNeededAsync<T>(
            IEnumerable<T> objects, CancellationToken cancellationToken) where T : AVObject {
            return EnqueueForAll(objects.Cast<AVObject>(), (Task toAwait) => {
                return FetchAllInternalAsync(objects, false, toAwait, cancellationToken);
            }, cancellationToken);
        }

        /// <summary>
        /// Fetches all of the objects in the provided list.
        /// </summary>
        /// <param name="objects">The objects to fetch.</param>
        /// <returns>The list passed in for convenience.</returns>
        public static Task<IEnumerable<T>> FetchAllAsync<T>(
            IEnumerable<T> objects) where T : AVObject {
            return FetchAllAsync(objects, CancellationToken.None);
        }

        /// <summary>
        /// Fetches all of the objects in the provided list.
        /// </summary>
        /// <param name="objects">The objects to fetch.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The list passed in for convenience.</returns>
        public static Task<IEnumerable<T>> FetchAllAsync<T>(
            IEnumerable<T> objects, CancellationToken cancellationToken) where T : AVObject {
            return EnqueueForAll(objects.Cast<AVObject>(), (Task toAwait) => {
                return FetchAllInternalAsync(objects, true, toAwait, cancellationToken);
            }, cancellationToken);
        }

        /// <summary>
        /// Fetches all of the objects in the list.
        /// </summary>
        /// <param name="objects">The objects to fetch.</param>
        /// <param name="force">If false, only objects without data will be fetched.</param>
        /// <param name="toAwait">A task to await before starting.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The list passed in for convenience.</returns>
        private static Task<IEnumerable<T>> FetchAllInternalAsync<T>(
            IEnumerable<T> objects, bool force, Task toAwait, CancellationToken cancellationToken) where T : AVObject {
            return toAwait.OnSuccess(_ => {
                if (objects.Any(obj => { return obj.state.ObjectId == null; })) {
                    throw new InvalidOperationException("You cannot fetch objects that haven't already been saved.");
                }

                var objectsToFetch = (from obj in objects
                                      where force || !obj.IsDataAvailable
                                      select obj).ToList();

                if (objectsToFetch.Count == 0) {
                    return Task.FromResult(objects);
                }

                // Do one Find for each class.
                var findsByClass =
                  (from obj in objectsToFetch
                   group obj.ObjectId by obj.ClassName into classGroup
                   where classGroup.Count() > 0
                   select new {
                       ClassName = classGroup.Key,
                       FindTask = new AVQuery<AVObject>(classGroup.Key)
                       .WhereContainedIn("objectId", classGroup)
                       .FindAsync(cancellationToken)
                   }).ToDictionary(pair => pair.ClassName, pair => pair.FindTask);

                // Wait for all the Finds to complete.
                return Task.WhenAll(findsByClass.Values.ToList()).OnSuccess(__ => {
                    if (cancellationToken.IsCancellationRequested) {
                        return objects;
                    }

                    // Merge the data from the Finds into the input objects.
                    var pairs = from obj in objectsToFetch
                                from result in findsByClass[obj.ClassName].Result
                                where result.ObjectId == obj.ObjectId
                                select new { obj, result };
                    foreach (var pair in pairs) {
                        pair.obj.MergeFromObject(pair.result);
                        pair.obj.hasBeenFetched = true;
                    }

                    return objects;
                });
            }).Unwrap();
        }

        #endregion

        #region Delete Object

        internal Task DeleteAsync(Task toAwait, CancellationToken cancellationToken) {
            if (ObjectId == null) {
                return Task.FromResult(0);
            }
            
            return toAwait.OnSuccess(_ => {
                return ObjectController.DeleteAsync(State, cancellationToken);
            }).Unwrap().OnSuccess(_ => IsDirty = true);
        }

        /// <summary>
        /// Deletes this object on the server.
        /// </summary>
        public Task DeleteAsync() {
            return DeleteAsync(CancellationToken.None);
        }

        /// <summary>
        /// Deletes this object on the server.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        public Task DeleteAsync(CancellationToken cancellationToken) {
            return taskQueue.Enqueue(toAwait => DeleteAsync(toAwait, cancellationToken),
              cancellationToken);
        }

        /// <summary>
        /// Deletes each object in the provided list.
        /// </summary>
        /// <param name="objects">The objects to delete.</param>
        public static Task DeleteAllAsync<T>(IEnumerable<T> objects) where T : AVObject {
            return DeleteAllAsync(objects, CancellationToken.None);
        }

        /// <summary>
        /// Deletes each object in the provided list.
        /// </summary>
        /// <param name="objects">The objects to delete.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public static Task DeleteAllAsync<T>(
            IEnumerable<T> objects, CancellationToken cancellationToken) where T : AVObject {
            var uniqueObjects = new HashSet<AVObject>(objects.OfType<AVObject>().ToList(),
              new IdentityEqualityComparer<AVObject>());

            return EnqueueForAll<object>(uniqueObjects, toAwait => {
                var states = uniqueObjects.Select(t => t.state).ToList();
                return toAwait.OnSuccess(_ => {
                    var deleteTasks = ObjectController.DeleteAllAsync(states, cancellationToken);
                    return Task.WhenAll(deleteTasks);
                }).Unwrap().OnSuccess(t => {
                    // Dirty all objects in memory.
                    foreach (var obj in uniqueObjects) {
                        obj.IsDirty = true;
                    }

                    return (object)null;
                });
            }, cancellationToken);
        }

        #endregion

        private static void CollectDirtyChildren(object node,
            IList<AVObject> dirtyChildren,
            ICollection<AVObject> seen,
            ICollection<AVObject> seenNew) {
            foreach (var obj in DeepTraversal(node).OfType<AVObject>()) {
                ICollection<AVObject> scopedSeenNew;
                // Check for cycles of new objects. Any such cycle means it will be impossible to save
                // this collection of objects, so throw an exception.
                if (obj.ObjectId != null) {
                    scopedSeenNew = new HashSet<AVObject>(new IdentityEqualityComparer<AVObject>());
                } else {
                    if (seenNew.Contains(obj)) {
                        throw new InvalidOperationException("Found a circular dependency while saving");
                    }
                    scopedSeenNew = new HashSet<AVObject>(seenNew, new IdentityEqualityComparer<AVObject>());
                    scopedSeenNew.Add(obj);
                }

                // Check for cycles of any object. If this occurs, then there's no problem, but
                // we shouldn't recurse any deeper, because it would be an infinite recursion.
                if (seen.Contains(obj)) {
                    return;
                }
                seen.Add(obj);

                // Recurse into this object's children looking for dirty children.
                // We only need to look at the child object's current estimated data,
                // because that's the only data that might need to be saved now.
                CollectDirtyChildren(obj.estimatedData, dirtyChildren, seen, scopedSeenNew);

                if (obj.CheckIsDirty(false)) {
                    dirtyChildren.Add(obj);
                }
            }
        }

        /// <summary>
        /// Helper version of CollectDirtyChildren so that callers don't have to add the internally
        /// used parameters.
        /// </summary>
        private static void CollectDirtyChildren(object node, IList<AVObject> dirtyChildren) {
            CollectDirtyChildren(node,
                dirtyChildren,
                new HashSet<AVObject>(new IdentityEqualityComparer<AVObject>()),
                new HashSet<AVObject>(new IdentityEqualityComparer<AVObject>()));
        }

        /// <summary>
        /// Returns true if the given object can be serialized for saving as a value
        /// that is pointed to by a AVObject.
        /// </summary>
        private static bool CanBeSerializedAsValue(object value) {
            return DeepTraversal(value, yieldRoot: true)
              .OfType<AVObject>()
              .All(o => o.ObjectId != null);
        }

        private bool CanBeSerialized {
            get {
                // This method is only used for batching sets of objects for saveAll
                // and when saving children automatically. Since it's only used to
                // determine whether or not save should be called on them, it only
                // needs to examine their current values, so we use estimatedData.
                lock (mutex) {
                    return CanBeSerializedAsValue(estimatedData);
                }
            }
        }

        /// <summary>
        /// Adds a task to the queue for all of the given objects.
        /// </summary>
        private static Task<T> EnqueueForAll<T>(IEnumerable<AVObject> objects,
            Func<Task, Task<T>> taskStart, CancellationToken cancellationToken) {
            // The task that will be complete when all of the child queues indicate they're ready to start.
            var readyToStart = new TaskCompletionSource<object>();

            // First, we need to lock the mutex for the queue for every object. We have to hold this
            // from at least when taskStart() is called to when obj.taskQueue enqueue is called, so
            // that saves actually get executed in the order they were setup by taskStart().
            // The locks have to be sorted so that we always acquire them in the same order.
            // Otherwise, there's some risk of deadlock.
            var lockSet = new LockSet(objects.Select(o => o.taskQueue.Mutex));

            lockSet.Enter();
            try {

                // The task produced by taskStart. By running this immediately, we allow everything prior
                // to toAwait to run before waiting for all of the queues on all of the objects.
                Task<T> fullTask = taskStart(readyToStart.Task);

                // Add fullTask to each of the objects' queues.
                var childTasks = new List<Task>();
                foreach (AVObject obj in objects) {
                    obj.taskQueue.Enqueue((Task task) => {
                        childTasks.Add(task);
                        return fullTask;
                    }, cancellationToken);
                }

                // When all of the objects' queues are ready, signal fullTask that it's ready to go on.
                Task.WhenAll(childTasks.ToArray()).ContinueWith((Task task) => {
                    readyToStart.SetResult(null);
                });

                return fullTask;
            } finally {
                lockSet.Exit();
            }
        }

        /// <summary>
        /// Removes a key from the object's data if it exists.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        public virtual void Remove(string key) {
            lock (mutex) {
                CheckKeyIsMutable(key);

                PerformOperation(key, AVDeleteOperation.Instance);
            }
        }


        private IEnumerable<string> ApplyOperations(IDictionary<string,
            IAVFieldOperation> operations,
            IDictionary<string, object> map) {
            List<string> appliedKeys = new List<string>();
            lock (mutex) {
                foreach (var pair in operations) {
                    object oldValue;
                    map.TryGetValue(pair.Key, out oldValue);
                    var newValue = pair.Value.Apply(oldValue, pair.Key);
                    if (newValue != AVDeleteOperation.DeleteToken) {
                        map[pair.Key] = newValue;
                    } else {
                        map.Remove(pair.Key);
                    }
                    appliedKeys.Add(pair.Key);
                }
            }
            return appliedKeys;
        }

        /// <summary>
        /// Regenerates the estimatedData map from the serverData and operations.
        /// </summary>
        internal void RebuildEstimatedData() {
            IEnumerable<string> changedKeys = null;

            lock (mutex) {
                //estimatedData.Clear();
                List<string> converdKeys = new List<string>();
                foreach (var item in state) {
                    var key = item.Key;
                    var value = item.Value;
                    if (!estimatedData.ContainsKey(key)) {
                        converdKeys.Add(key);
                    } else {
                        var oldValue = estimatedData[key];
                        if (oldValue != value) {
                            converdKeys.Add(key);
                        }
                        estimatedData.Remove(key);
                    }
                    estimatedData.Add(item);
                }
                changedKeys = converdKeys;
                foreach (var operations in operationSetQueue) {
                    var appliedKeys = ApplyOperations(operations, estimatedData);
                    changedKeys = converdKeys.Concat(appliedKeys);
                }
                // We've just applied a bunch of operations to estimatedData which
                // may have changed all of its keys. Notify of all keys and properties
                // mapped to keys being changed.
                OnFieldsChanged(changedKeys);
            }
        }

        /// <summary>
        /// PerformOperation is like setting a value at an index, but instead of
        /// just taking a new value, it takes a AVFieldOperation that modifies the value.
        /// </summary>
        internal void PerformOperation(string key, IAVFieldOperation operation) {
            lock (mutex) {
                var ifDirtyBeforePerform = this.IsDirty;
                object oldValue;
                estimatedData.TryGetValue(key, out oldValue);
                object newValue = operation.Apply(oldValue, key);
                if (newValue != AVDeleteOperation.DeleteToken) {
                    estimatedData[key] = newValue;
                } else {
                    estimatedData.Remove(key);
                }

                IAVFieldOperation oldOperation;
                bool wasDirty = CurrentOperations.Count > 0;
                CurrentOperations.TryGetValue(key, out oldOperation);
                var newOperation = operation.MergeWithPrevious(oldOperation);
                CurrentOperations[key] = newOperation;
                if (!wasDirty) {
                    OnPropertyChanged("IsDirty");
                    if (ifDirtyBeforePerform != wasDirty) {
                        OnPropertyUpdated("IsDirty", ifDirtyBeforePerform, wasDirty);
                    }
                }

                OnFieldsChanged(new[] { key });
                OnPropertyUpdated(key, oldValue, newValue);
            }
        }

        /// <summary>
        /// Override to run validations on key/value pairs. Make sure to still
        /// call the base version.
        /// </summary>
        internal virtual void OnSettingValue(ref string key, ref object value) {
            if (key == null) {
                throw new ArgumentNullException("key");
            }
        }

        /// <summary>
        /// Gets or sets a value on the object. It is recommended to name
        /// keys in partialCamelCaseLikeThis.
        /// </summary>
        /// <param name="key">The key for the object. Keys must be alphanumeric plus underscore
        /// and start with a letter.</param>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The property is
        /// retrieved and <paramref name="key"/> is not found.</exception>
        /// <returns>The value for the key.</returns>
        virtual public object this[string key] {
            get {
                lock (mutex) {
                    CheckGetAccess(key);

                    var value = estimatedData[key];

                    // A relation may be deserialized without a parent or key. Either way,
                    // make sure it's consistent.
                    var relation = value as AVRelationBase;
                    if (relation != null) {
                        relation.EnsureParentAndKey(this, key);
                    }

                    return value;
                }
            }
            set {
                lock (mutex) {
                    CheckKeyIsMutable(key);

                    Set(key, value);
                }
            }
        }

        /// <summary>
        /// Perform Set internally which is not gated by mutability check.
        /// </summary>
        /// <param name="key">key for the object.</param>
        /// <param name="value">the value for the key.</param>
        internal void Set(string key, object value) {
            lock (mutex) {
                OnSettingValue(ref key, ref value);

                if (!AVEncoder.IsValidType(value)) {
                    throw new ArgumentException("Invalid type for value: " + value.GetType().ToString());
                }

                PerformOperation(key, new AVSetOperation(value));
            }
        }

        internal void SetIfDifferent<T>(string key, T value) {
            T current;
            bool hasCurrent = TryGetValue<T>(key, out current);
            if (value == null) {
                if (hasCurrent) {
                    PerformOperation(key, AVDeleteOperation.Instance);
                }
                return;
            }
            if (!hasCurrent || !value.Equals(current)) {
                Set(key, value);
            }
        }

        #region Atomic Increment

        /// <summary>
        /// Atomically increments the given key by 1.
        /// </summary>
        /// <param name="key">The key to increment.</param>
        public void Increment(string key) {
            Increment(key, 1);
        }

        /// <summary>
        /// Atomically increments the given key by the given number.
        /// </summary>
        /// <param name="key">The key to increment.</param>
        /// <param name="amount">The amount to increment by.</param>
        public void Increment(string key, long amount) {
            lock (mutex) {
                CheckKeyIsMutable(key);

                PerformOperation(key, new AVIncrementOperation(amount));
            }
        }

        /// <summary>
        /// Atomically increments the given key by the given number.
        /// </summary>
        /// <param name="key">The key to increment.</param>
        /// <param name="amount">The amount to increment by.</param>
        public void Increment(string key, double amount) {
            lock (mutex) {
                CheckKeyIsMutable(key);

                PerformOperation(key, new AVIncrementOperation(amount));
            }
        }

        #endregion

        /// <summary>
        /// Atomically adds an object to the end of the list associated with the given key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The object to add.</param>
        public void AddToList(string key, object value) {
            AddRangeToList(key, new[] { value });
        }

        /// <summary>
        /// Atomically adds objects to the end of the list associated with the given key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="values">The objects to add.</param>
        public void AddRangeToList<T>(string key, IEnumerable<T> values) {
            lock (mutex) {
                CheckKeyIsMutable(key);

                PerformOperation(key, new AVAddOperation(values.Cast<object>()));

                OnCollectionPropertyUpdated(key, NotifyCollectionUpdatedAction.Add, null, values);
            }
        }

        /// <summary>
        /// Atomically adds an object to the end of the list associated with the given key,
        /// only if it is not already present in the list. The position of the insert is not
        /// guaranteed.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The object to add.</param>
        public void AddUniqueToList(string key, object value) {
            AddRangeUniqueToList(key, new object[] { value });
        }

        /// <summary>
        /// Atomically adds objects to the end of the list associated with the given key,
        /// only if they are not already present in the list. The position of the inserts are not
        /// guaranteed.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="values">The objects to add.</param>
        public void AddRangeUniqueToList<T>(string key, IEnumerable<T> values) {
            lock (mutex) {
                CheckKeyIsMutable(key);

                PerformOperation(key, new AVAddUniqueOperation(values.Cast<object>()));
            }
        }

        /// <summary>
        /// Atomically removes all instances of the objects in <paramref name="values"/>
        /// from the list associated with the given key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="values">The objects to remove.</param>
        public void RemoveAllFromList<T>(string key, IEnumerable<T> values) {
            lock (mutex) {
                CheckKeyIsMutable(key);

                PerformOperation(key, new AVRemoveOperation(values.Cast<object>()));

                OnCollectionPropertyUpdated(key, NotifyCollectionUpdatedAction.Remove, values, null);
            }
        }

        /// <summary>
        /// Returns whether this object has a particular key.
        /// </summary>
        /// <param name="key">The key to check for</param>
        public bool ContainsKey(string key) {
            lock (mutex) {
                return estimatedData.ContainsKey(key);
            }
        }

        /// <summary>
        /// Gets a value for the key of a particular type.
        /// <typeparam name="T">The type to convert the value to. Supported types are
        /// AVObject and its descendents, LeanCloud types such as AVRelation and AVGeopoint,
        /// primitive types,IList&lt;T&gt;, IDictionary&lt;string, T&gt;, and strings.</typeparam>
        /// <param name="key">The key of the element to get.</param>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The property is
        /// retrieved and <paramref name="key"/> is not found.</exception>
        /// </summary>
        public T Get<T>(string key) {
            return Conversion.To<T>(this[key]);
        }

        /// <summary>
        /// Access or create a Relation value for a key.
        /// </summary>
        /// <typeparam name="T">The type of object to create a relation for.</typeparam>
        /// <param name="key">The key for the relation field.</param>
        /// <returns>A AVRelation for the key.</returns>
        public AVRelation<T> GetRelation<T>(string key) where T : AVObject {
            // All the sanity checking is done when add or remove is called.
            AVRelation<T> relation = null;
            TryGetValue(key, out relation);
            return relation ?? new AVRelation<T>(this, key);
        }

        /// <summary>
        /// Get relation revserse query.
        /// </summary>
        /// <typeparam name="T">AVObject</typeparam>
        /// <param name="parentClassName">parent className</param>
        /// <param name="key">key</param>
        /// <returns></returns>
        public AVQuery<T> GetRelationRevserseQuery<T>(string parentClassName, string key) where T : AVObject {
            if (string.IsNullOrEmpty(parentClassName)) {
                throw new ArgumentNullException("parentClassName", "can not query a relation without parentClassName.");
            }
            if (string.IsNullOrEmpty(key)) {
                throw new ArgumentNullException("key", "can not query a relation without key.");
            }
            return new AVQuery<T>(parentClassName).WhereEqualTo(key, this);
        }

        /// <summary>
        /// Populates result with the value for the key, if possible.
        /// </summary>
        /// <typeparam name="T">The desired type for the value.</typeparam>
        /// <param name="key">The key to retrieve a value for.</param>
        /// <param name="result">The value for the given key, converted to the
        /// requested type, or null if unsuccessful.</param>
        /// <returns>true if the lookup and conversion succeeded, otherwise
        /// false.</returns>
        public virtual bool TryGetValue<T>(string key, out T result) {
            lock (mutex) {
                if (ContainsKey(key)) {
                    try {
                        var temp = Conversion.To<T>(this[key]);
                        result = temp;
                        return true;
                    } catch (InvalidCastException) {
                        result = default(T);
                        return false;
                    }
                }
                result = default(T);
                return false;
            }
        }

        /// <summary>
        /// Gets whether the AVObject has been fetched.
        /// </summary>
        public bool IsDataAvailable {
            get {
                lock (mutex) {
                    return hasBeenFetched;
                }
            }
        }

        private bool CheckIsDataAvailable(string key) {
            lock (mutex) {
                return IsDataAvailable || estimatedData.ContainsKey(key);
            }
        }

        private void CheckGetAccess(string key) {
            lock (mutex) {
                if (!CheckIsDataAvailable(key)) {
                    throw new InvalidOperationException(
                        "AVObject has no data for this key. Call FetchIfNeededAsync() to get the data.");
                }
            }
        }

        private void CheckKeyIsMutable(string key) {
            if (!IsKeyMutable(key)) {
                throw new InvalidOperationException(
                  "Cannot change the `" + key + "` property of a `" + ClassName + "` object.");
            }
        }

        protected virtual bool IsKeyMutable(string key) {
            return true;
        }

        /// <summary>
        /// A helper function for checking whether two AVObjects point to
        /// the same object in the cloud.
        /// </summary>
        public bool HasSameId(AVObject other) {
            lock (mutex) {
                return other != null &&
                    object.Equals(ClassName, other.ClassName) &&
                    object.Equals(ObjectId, other.ObjectId);
            }
        }

        internal IDictionary<string, IAVFieldOperation> CurrentOperations {
            get {
                lock (mutex) {
                    return operationSetQueue.Last.Value;
                }
            }
        }

        /// <summary>
        /// Gets a set view of the keys contained in this object. This does not include createdAt,
        /// updatedAt, or objectId. It does include things like username and ACL.
        /// </summary>
        public ICollection<string> Keys {
            get {
                lock (mutex) {
                    return estimatedData.Keys;
                }
            }
        }

        /// <summary>
        /// Gets or sets the AVACL governing this object.
        /// </summary>
        [AVFieldName("ACL")]
        public AVACL ACL {
            get { return GetProperty<AVACL>(null, "ACL"); }
            set { SetProperty(value, "ACL"); }
        }

        /// <summary>
        /// Returns true if this object was created by the LeanCloud server when the
        /// object might have already been there (e.g. in the case of a Facebook
        /// login)
        /// </summary>
#if !UNITY
        public
#else
    internal
#endif
 bool IsNew {
            get {
                return state.IsNew;
            }
#if !UNITY
            internal
#endif
      set {
                MutateState(mutableClone => {
                    mutableClone.IsNew = value;
                });
                OnPropertyChanged("IsNew");
            }
        }

        /// <summary>
        /// Gets the last time this object was updated as the server sees it, so that if you make changes
        /// to a AVObject, then wait a while, and then call <see cref="SaveAsync()"/>, the updated time
        /// will be the time of the <see cref="SaveAsync()"/> call rather than the time the object was
        /// changed locally.
        /// </summary>
        [AVFieldName("updatedAt")]
        public DateTime? UpdatedAt {
            get {
                return state.UpdatedAt;
            }
        }

        /// <summary>
        /// Gets the first time this object was saved as the server sees it, so that if you create a
        /// AVObject, then wait a while, and then call <see cref="SaveAsync()"/>, the
        /// creation time will be the time of the first <see cref="SaveAsync()"/> call rather than
        /// the time the object was created locally.
        /// </summary>
        [AVFieldName("createdAt")]
        public DateTime? CreatedAt {
            get {
                return state.CreatedAt;
            }
        }

        public bool FetchWhenSave {
            get; set;
        }

        /// <summary>
        /// Indicates whether this AVObject has unsaved changes.
        /// </summary>
        public bool IsDirty {
            get {
                lock (mutex) { return CheckIsDirty(true); }
            }
            internal set {
                lock (mutex) {
                    dirty = value;
                    OnPropertyChanged("IsDirty");
                }
            }
        }

        /// <summary>
        /// Indicates whether key is unsaved for this AVObject.
        /// </summary>
        /// <param name="key">The key to check for.</param>
        /// <returns><c>true</c> if the key has been altered and not saved yet, otherwise
        /// <c>false</c>.</returns>
        public bool IsKeyDirty(string key) {
            lock (mutex) {
                return CurrentOperations.ContainsKey(key);
            }
        }

        private bool CheckIsDirty(bool considerChildren) {
            lock (mutex) {
                return (dirty || CurrentOperations.Count > 0 || (considerChildren && HasDirtyChildren));
            }
        }

        /// <summary>
        /// Gets or sets the object id. An object id is assigned as soon as an object is
        /// saved to the server. The combination of a <see cref="ClassName"/> and an
        /// <see cref="ObjectId"/> uniquely identifies an object in your application.
        /// </summary>
        [AVFieldName("objectId")]
        public string ObjectId {
            get {
                return state.ObjectId;
            }
            set {
                IsDirty = true;
                SetObjectIdInternal(value);
            }
        }
        /// <summary>
        /// Sets the objectId without marking dirty.
        /// </summary>
        /// <param name="objectId">The new objectId</param>
        private void SetObjectIdInternal(string objectId) {
            lock (mutex) {
                MutateState(mutableClone => {
                    mutableClone.ObjectId = objectId;
                });
                OnPropertyChanged("ObjectId");
            }
        }

        /// <summary>
        /// Gets the class name for the AVObject.
        /// </summary>
        public string ClassName {
            get {
                return state.ClassName;
            }
        }

        /// <summary>
        /// Adds a value for the given key, throwing an Exception if the key
        /// already has a value.
        /// </summary>
        /// <remarks>
        /// This allows you to use collection initialization syntax when creating AVObjects,
        /// such as:
        /// <code>
        /// var obj = new AVObject("MyType")
        /// {
        ///     {"name", "foo"},
        ///     {"count", 10},
        ///     {"found", false}
        /// };
        /// </code>
        /// </remarks>
        /// <param name="key">The key for which a value should be set.</param>
        /// <param name="value">The value for the key.</param>
        public void Add(string key, object value) {
            lock (mutex) {
                if (this.ContainsKey(key)) {
                    throw new ArgumentException("Key already exists", key);
                }
                this[key] = value;
            }
        }

        IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>
            .GetEnumerator() {
            lock (mutex) {
                return estimatedData.GetEnumerator();
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
            lock (mutex) {
                return ((IEnumerable<KeyValuePair<string, object>>)this).GetEnumerator();
            }
        }

        /// <summary>
        /// Gets a <see cref="AVQuery{AVObject}"/> for the type of object specified by
        /// <paramref name="className"/>
        /// </summary>
        /// <param name="className">The class name of the object.</param>
        /// <returns>A new <see cref="AVQuery{AVObject}"/>.</returns>
        public static AVQuery<AVObject> GetQuery(string className) {
            // Since we can't return a AVQuery<AVUser> (due to strong-typing with
            // generics), we'll require you to go through subclasses. This is a better
            // experience anyway, especially with LINQ integration, since you'll get
            // strongly-typed queries and compile-time checking of property names and
            // types.
            if (SubclassingController.GetType(className) != null) {
                throw new ArgumentException(
                  "Use the class-specific query properties for class " + className, "className");
            }
            return new AVQuery<AVObject>(className);
        }

        /// <summary>
        /// Raises change notifications for all properties associated with the given
        /// field names. If fieldNames is null, this will notify for all known field-linked
        /// properties (e.g. this happens when we recalculate all estimated data from scratch)
        /// </summary>
        protected virtual void OnFieldsChanged(IEnumerable<string> fieldNames) {
            var mappings = SubclassingController.GetPropertyMappings(ClassName);
            IEnumerable<string> properties;

            if (fieldNames != null && mappings != null) {
                properties = from m in mappings
                             join f in fieldNames on m.Value equals f
                             select m.Key;
            } else if (mappings != null) {
                properties = mappings.Keys;
            } else {
                properties = Enumerable.Empty<string>();
            }

            foreach (var property in properties) {
                OnPropertyChanged(property);
            }
            OnPropertyChanged("Item[]");
        }

        /// <summary>
        /// Raises change notifications for a property. Passing null or the empty string
        /// notifies the binding framework that all properties/indexes have changed.
        /// Passing "Item[]" tells the binding framework that all indexed values
        /// have changed (but not all properties)
        /// </summary>
        protected virtual void OnPropertyChanged(
#if !UNITY
[CallerMemberName] string propertyName = null
#else
string propertyName
#endif
) {
            propertyChanged.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private SynchronizedEventHandler<PropertyChangedEventArgs> propertyChanged =
            new SynchronizedEventHandler<PropertyChangedEventArgs>();
        /// <summary>
        /// Occurs when a property value changes.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged {
            add {
                propertyChanged.Add(value);
            }
            remove {
                propertyChanged.Remove(value);
            }
        }

        private SynchronizedEventHandler<PropertyUpdatedEventArgs> propertyUpdated =
    new SynchronizedEventHandler<PropertyUpdatedEventArgs>();

        public event PropertyUpdatedEventHandler PropertyUpdated {
            add {
                propertyUpdated.Add(value);
            }
            remove {
                propertyUpdated.Remove(value);
            }
        }

        protected virtual void OnPropertyUpdated(string propertyName, object newValue, object oldValue) {
            propertyUpdated.Invoke(this, new PropertyUpdatedEventArgs(propertyName, oldValue, newValue));
        }

        private SynchronizedEventHandler<CollectionPropertyUpdatedEventArgs> collectionUpdated =
new SynchronizedEventHandler<CollectionPropertyUpdatedEventArgs>();

        public event CollectionPropertyUpdatedEventHandler CollectionPropertyUpdated {
            add {
                collectionUpdated.Add(value);
            }
            remove {
                collectionUpdated.Remove(value);
            }
        }

        protected virtual void OnCollectionPropertyUpdated(string propertyName, NotifyCollectionUpdatedAction action, IEnumerable oldValues, IEnumerable newValues) {
            collectionUpdated?.Invoke(this, new CollectionPropertyUpdatedEventArgs(propertyName, action, oldValues, newValues));
        }
    }

    public interface INotifyPropertyUpdated {
        event PropertyUpdatedEventHandler PropertyUpdated;
    }

    public interface INotifyCollectionPropertyUpdated {
        event CollectionPropertyUpdatedEventHandler CollectionPropertyUpdated;
    }

    public enum NotifyCollectionUpdatedAction {
        Add,
        Remove
    }

    public class CollectionPropertyUpdatedEventArgs : PropertyChangedEventArgs {
        public CollectionPropertyUpdatedEventArgs(string propertyName, NotifyCollectionUpdatedAction collectionAction, IEnumerable oldValues, IEnumerable newValues) : base(propertyName) {
            CollectionAction = collectionAction;
            OldValues = oldValues;
            NewValues = newValues;
        }

        public IEnumerable OldValues { get; set; }

        public IEnumerable NewValues { get; set; }

        public NotifyCollectionUpdatedAction CollectionAction { get; set; }
    }

    public class PropertyUpdatedEventArgs : PropertyChangedEventArgs {
        public PropertyUpdatedEventArgs(string propertyName, object oldValue, object newValue) : base(propertyName) {
            OldValue = oldValue;
            NewValue = newValue;
        }

        public object OldValue { get; private set; }
        public object NewValue { get; private set; }
    }

    public delegate void PropertyUpdatedEventHandler(object sender, PropertyUpdatedEventArgs args);

    public delegate void CollectionPropertyUpdatedEventHandler(object sender, CollectionPropertyUpdatedEventArgs args);
}
