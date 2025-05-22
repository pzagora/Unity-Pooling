using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Commons.Constants;
using DependencyInjection.Attributes;
using DependencyInjection.Extensions;
using UnityEngine;
using UnityEngine.Pool;
using Commons.Enums;
using Commons.Extensions;
using Commons.Services;
using Object = UnityEngine.Object;

namespace Pooling
{
    /// <summary>
    /// A simple generic class to simplify object pooling in Unity.
    /// One can derive from this class for more advanced usages.
    /// </summary>
    /// <typeparam name="T">A <see cref="MonoBehaviour"/> to perform pooling on.</typeparam>
    public class GenericObjectPool<T> : IDisposable where T : Object
    {
        #region DI

        [Inject] private readonly ICoroutineService _coroutineService;

        #endregion
        
        #region FIELDS

        private readonly T _prefab;
        private readonly string _parentObjectName;
        private readonly GameObject _parentObject;
        private readonly Transform _parentTransform;
        private readonly Dictionary<T, ObjectPoolStatus> _createdPoolObjects = new();
        private readonly int _timeToLive;

        protected Action OnPoolChanged;
        protected bool KeepAliveIndefinitely => false;

        private ObjectPool<T> _pool;
        private Guid _disposalCoroutineId = Guid.Empty;
        private int _currentTimeToLive;

        #endregion
        
        #region CONSTANTS
        
        private const int DISPOSAL_TICK = 1;
        private const int MAX_DISPOSAL_TIME = 300;

        #endregion
        
        #region PROPERTIES

        private ObjectPool<T> Pool
        {
            get
            {
                if (_pool.IsNull())
                    throw new NullReferenceException(
                        string.Format(Msg.NOT_INITIALIZED, this, nameof(Pool)));

                return _pool;
            }
            set => _pool = value;
        }
        
        private int CurrentTimeToLive
        {
            get => _currentTimeToLive;
            set
            {
                if (_currentTimeToLive == value)
                    return;

                _currentTimeToLive = Mathf.Clamp(value, 0, _timeToLive);
            }
        }

        #endregion

        #region CONSTRUCTORS

        /// <summary>
        /// Default constructor for generic pool class.
        /// Creates custom parent object to reduce hierarchy clutter.
        /// </summary>
        /// <param name="prefab">Pool of this object and it's type will be created.</param>
        /// <param name="timeToLive">Time in seconds after which unused pool will be disposed (between <see cref="DISPOSAL_TICK"/> and <see cref="MAX_DISPOSAL_TIME"/>)</param>
        /// <param name="initial">Initial pool size.</param>
        /// <param name="max">Max pool size.</param>
        /// <param name="collectionChecks">Should throw exception when releasing already released object from pool.</param>
        public GenericObjectPool(T prefab, int timeToLive = 30, int initial = 0, int max = 10, bool collectionChecks = false)
            : this(prefab, new GameObject(string.Format(Msg.OBJECT_POOL_PARENT_NAME, prefab.name)), 
                timeToLive, initial, max, collectionChecks) { }

        /// <summary>
        /// Constructor with parent transform for generic pool class.
        /// </summary>
        /// <param name="prefab">Pool of this object and it's type will be created.</param>
        /// <param name="parent">GameObject parent for pool object spawn.</param>
        /// <param name="timeToLive">Time in seconds after which unused pool will be disposed (between <see cref="DISPOSAL_TICK"/> and <see cref="MAX_DISPOSAL_TIME"/>)</param>
        /// <param name="initial">Initial pool size.</param>
        /// <param name="max">Max pool size.</param>
        /// <param name="collectionChecks">Should throw exception when releasing already released object from pool.</param>
        public GenericObjectPool(T prefab, GameObject parent, int timeToLive = 30, int initial = 0, int max = 10, bool collectionChecks = false)
            : this(prefab, parent.transform, timeToLive, initial, max, collectionChecks) { }

        /// <summary>
        /// Constructor with parent transform for generic pool class.
        /// </summary>
        /// <param name="prefab">Pool of this object and it's type will be created.</param>
        /// <param name="parent">Transform parent for pool object spawn.</param>
        /// <param name="timeToLive">Time in seconds after which unused pool will be disposed (between <see cref="DISPOSAL_TICK"/> and <see cref="MAX_DISPOSAL_TIME"/>)</param>
        /// <param name="initial">Initial pool size.</param>
        /// <param name="max">Max pool size.</param>
        /// <param name="collectionChecks">Should throw exception when releasing already released object from pool.</param>
        public GenericObjectPool(T prefab, Transform parent, int timeToLive = 30, int initial = 0, int max = 10, bool collectionChecks = false)
        {
            this.InjectAttributes();
            
            _prefab = prefab;
            _parentObject = parent.gameObject;
            _parentTransform = parent;
            _parentObjectName = parent.name;
            _timeToLive = Mathf.Clamp(timeToLive, DISPOSAL_TICK, MAX_DISPOSAL_TIME);

            OnPoolChanged += DelayedDispose;
            OnPoolChanged += UpdateHierarchyName;
            
            CreatePool(initial, max, collectionChecks);
        }

        #endregion

        #region PUBLIC METHODS

        /// <summary>
        /// Method that gets next available object from object pool.
        /// One can change <see cref="Get"/> method by overriding <see cref="GetSetup"/> method.
        /// </summary>
        /// <returns>Next available object of pool type.</returns>
        public T Get() => Pool.Get();

        /// <summary>
        /// Method that releases given object back to the pool.
        /// One can change <see cref="Release"/> method by overriding <see cref="ReleaseSetup"/> method.
        /// </summary>
        /// <param name="poolItem">Item to return to the pool.</param>
        public void Release(T poolItem) => Pool.Release(poolItem);

        /// <summary>
        /// Method that releases all active pool objects back to the pool using <see cref="Release"/> method.
        /// </summary>
        public void ReleaseAll()
        {
            var activePoolObjects = _createdPoolObjects
                .Where(poolItem => poolItem.Value == ObjectPoolStatus.Active)
                .Select(poolItem => poolItem.Key)
                .ToList();

            foreach (var activePoolObject in activePoolObjects)
            {
                Release(activePoolObject);
            }
        }

        /// <summary>
        /// Returns amount of pool objects with status Active
        /// </summary>
        public int ActiveCount => _createdPoolObjects
            .Count(cpo => cpo.Value == ObjectPoolStatus.Active);
        
        /// <summary>
        /// Returns amount of pool objects with status Inactive
        /// </summary>
        public int InactiveCount => _createdPoolObjects
            .Count(cpo => cpo.Value == ObjectPoolStatus.Inactive);

        /// <summary>
        /// Returns amount of pool objects, both Active and Inactive
        /// </summary>
        public int Count => _createdPoolObjects.Count;

        #endregion

        #region PROTECTED VIRTUAL METHODS

        protected virtual T CreateSetup() => Object.Instantiate(_prefab, _parentTransform);
        protected virtual void GetSetup(T poolItem) => HandleGenericSetup(poolItem, true);
        protected virtual void ReleaseSetup(T poolItem) => HandleGenericSetup(poolItem, false);
        protected virtual void DestroySetup(T poolItem) => Object.Destroy(poolItem);

        #endregion

        #region PRIVATE METHODS

        private void CreatePool(int defaultCapacity, int max, bool collectionChecks)
        {
            Pool = new ObjectPool<T>(
                CreateSetupWithTracking,
                GetSetupWithTracking,
                ReleaseSetupWithTracking,
                DestroySetupWithTracking,
                collectionChecks,
                defaultCapacity,
                max);
        }

        private T CreateSetupWithTracking()
        {
            var poolItem = CreateSetup();
            _createdPoolObjects.Add(poolItem, ObjectPoolStatus.Inactive);
            
            OnPoolChanged?.Invoke();
            return poolItem;
        }

        private void GetSetupWithTracking(T poolItem)
        {
            if (poolItem.IsNull())
                return;

            if (_createdPoolObjects.ContainsKey(poolItem))
                _createdPoolObjects[poolItem] = ObjectPoolStatus.Active;
            
            OnPoolChanged?.Invoke();
            GetSetup(poolItem);
        }

        private void ReleaseSetupWithTracking(T poolItem)
        {
            if (poolItem.IsNull())
                return;

            if (_createdPoolObjects.ContainsKey(poolItem))
                _createdPoolObjects[poolItem] = ObjectPoolStatus.Inactive;
            
            OnPoolChanged?.Invoke();
            ReleaseSetup(poolItem);
        }

        private void DestroySetupWithTracking(T poolItem)
        {
            if (poolItem.IsNull())
                return;

            if (_createdPoolObjects.ContainsKey(poolItem))
                _createdPoolObjects.Remove(poolItem);
            
            OnPoolChanged?.Invoke();
            DestroySetup(poolItem);
        }

        private void HandleGenericSetup(T poolItem, bool isEnabled)
        {
            switch (poolItem)
            {
                case GameObject gameObject:
                    gameObject.SetActive(isEnabled);
                    break;

                // Component includes MonoBehaviour
                case Component component:
                    component.gameObject.SetActive(isEnabled);
                    break;

                default:
                    var message = string.Format(Msg.OBJECT_POOL_TYPE_NOT_SUPPORTED, this, typeof(T));
                    throw new NotSupportedException(message);
            }
            
            OnPoolChanged?.Invoke();
        }

        private void UpdateHierarchyName()
        {
            _parentObject.name = _disposalCoroutineId == Guid.Empty
                ? string.Format(Msg.OBJECT_POOL_COUNT_NAME, _parentObjectName, ActiveCount, Count) 
                : string.Format(Msg.OBJECT_POOL_DISPOSE_NAME, _parentObjectName, ActiveCount, Count, CurrentTimeToLive);
        }
        
        #endregion
        
        #region DISPOSAL
        
        protected virtual void DelayedDispose()
        {
            if (KeepAliveIndefinitely || ActiveCount.NotZero() || Count.IsZero())
            {
                _coroutineService.StopCoroutine(ref _disposalCoroutineId);
                return;
            }

            if (_disposalCoroutineId != Guid.Empty)
                return;
            
            _disposalCoroutineId = _coroutineService.StartCoroutine(DisposalRoutine());
        }

        protected virtual IEnumerator DisposalRoutine()
        {
            CurrentTimeToLive = _timeToLive;
            while(CurrentTimeToLive > NumericExtensions.Zero)
            {
                yield return new WaitForSecondsRealtime(DISPOSAL_TICK);
                
                CurrentTimeToLive--;
                UpdateHierarchyName();
            }
            
            Dispose();
        }

        public void Dispose() => Pool.Dispose();

        #endregion
    }
}