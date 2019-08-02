using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace WeakEvent
{
    internal abstract class DelegateCollectionBase<TOpenEventHandler, TStrongHandler> : IEnumerable<WeakDelegate<TOpenEventHandler, TStrongHandler>>
        where TOpenEventHandler : Delegate
        where TStrongHandler : struct
    {
        #region Open handler generation and cache

        // ReSharper disable once StaticMemberInGenericType (by design)
        private static readonly ConcurrentDictionary<MethodInfo, TOpenEventHandler> OpenHandlerCache =
            new ConcurrentDictionary<MethodInfo, TOpenEventHandler>();

        private static readonly Type _eventArgsType = typeof(TOpenEventHandler)
            .GetRuntimeMethods()
            .Single(m => m.Name == "Invoke")
            .GetParameters()
            .Last()
            .ParameterType;

        private static TOpenEventHandler CreateOpenHandler(MethodInfo method)
        {
            var target = Expression.Parameter(typeof(object), "target");
            var sender = Expression.Parameter(typeof(object), "sender");
            var e = Expression.Parameter(_eventArgsType, "e");

            if (method.IsStatic)
            {
                var expr = Expression.Lambda<TOpenEventHandler>(
                    Expression.Call(
                        method,
                        sender, e),
                    target, sender, e);
                return expr.Compile();
            }
            else
            {
                var expr = Expression.Lambda<TOpenEventHandler>(
                    Expression.Call(
                        Expression.Convert(target, method.DeclaringType),
                        method,
                        sender, e),
                    target, sender, e);
                return expr.Compile();
            }
        }

        #endregion

        private List<WeakDelegate<TOpenEventHandler, TStrongHandler>> _delegates;

        private Dictionary<long, List<int>> _index;

        private int _deletedCount;

        private readonly Func<object, TOpenEventHandler, TStrongHandler> _createStrongHandler;

        public DelegateCollectionBase(Func<object, TOpenEventHandler, TStrongHandler> createStrongHandler)
        {
            _delegates = new List<WeakDelegate<TOpenEventHandler, TStrongHandler>>();
            _index = new Dictionary<long, List<int>>();
            _createStrongHandler = createStrongHandler;
        }

        public void Add(Delegate singleHandler)
        {
            var openHandler = OpenHandlerCache.GetOrAdd(singleHandler.GetMethodInfo(), CreateOpenHandler);
            _delegates.Add(new WeakDelegate<TOpenEventHandler, TStrongHandler>(singleHandler, openHandler, _createStrongHandler));
            var index = _delegates.Count - 1;
            AddToIndex(singleHandler, index);
        }

        public void Remove(Delegate singleHandler)
        {
            var hashCode = GetDelegateHashCode(singleHandler);

            if (!_index.ContainsKey(hashCode))
                return;

            var indices = _index[hashCode];
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                int index = indices[i];
                if (_delegates[index] != null &&
                    _delegates[index].IsMatch(singleHandler))
                {
                    _delegates[index] = null;
                    _deletedCount++;
                    indices.Remove(i);
                }
            }

            if (indices.Count == 0)
                _index.Remove(hashCode);
        }

        public void Invalidate(int index)
        {
            _delegates[index] = null;
            _deletedCount++;
        }

        public void CollectDeleted()
        {
            if (_deletedCount < _delegates.Count / 4)
                return;

            Dictionary<int, int> newIndices = new Dictionary<int, int>();
            var newDelegates = new List<WeakDelegate<TOpenEventHandler, TStrongHandler>>();
            int oldIndex = 0;
            int newIndex = 0;
            foreach (var item in _delegates)
            {
                if (item != null)
                {
                    newDelegates.Add(item);
                    newIndices.Add(oldIndex, newIndex);
                    newIndex++;
                }

                oldIndex++;
            }

            _delegates = newDelegates;

            var hashCodes = _index.Keys.ToList();
            foreach (var hashCode in hashCodes)
            {
                _index[hashCode] = _index[hashCode]
                    .Where(oi => newIndices.ContainsKey(oi))
                    .Select(oi => newIndices[oi]).ToList();
            }

            _deletedCount = 0;
        }

        public WeakDelegate<TOpenEventHandler, TStrongHandler> this[int index] => _delegates[index];

        /// <summary>Returns an enumerator that iterates through the collection.</summary>
        /// <returns>A <see cref="T:System.Collections.Generic.IEnumerator`1" /> that can be used to iterate through the collection.</returns>
        public IEnumerator<WeakDelegate<TOpenEventHandler, TStrongHandler>> GetEnumerator()
        {
            return _delegates.GetEnumerator();
        }

        /// <summary>Returns an enumerator that iterates through a collection.</summary>
        /// <returns>An <see cref="T:System.Collections.IEnumerator" /> object that can be used to iterate through the collection.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public int Count => _delegates.Count;

        private static int GetDelegateHashCode(Delegate handler)
        {
            var hashCode = -335093136;
            hashCode = hashCode * -1521134295 + (handler?.Target?.GetHashCode()).GetValueOrDefault();
            hashCode = hashCode * -1521134295 + (handler?.GetMethodInfo()?.GetHashCode()).GetValueOrDefault();
            return hashCode;
        }

        private void AddToIndex(Delegate singleHandler, int index)
        {
            var hashCode = GetDelegateHashCode(singleHandler);
            if (_index.ContainsKey(hashCode))
                _index[hashCode].Add(index);
            else
                _index.Add(hashCode, new List<int> { index });
        }
    }
}