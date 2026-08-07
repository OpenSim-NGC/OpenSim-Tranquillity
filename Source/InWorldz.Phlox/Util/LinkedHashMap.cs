using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections;

namespace InWorldz.Phlox.Util
{
    /// <summary>
    /// Stores a collection of objects that can be looked up by a key in O(1) and 
    /// also can be enumerated in insertion order
    /// </summary>
    /// <typeparam name="K">Key to index the entries by</typeparam>
    /// <typeparam name="T">The type of each entry</typeparam>
    public class LinkedHashMap<K,T> : IEnumerable<T>
    {
        LinkedList<T> _itemList = new LinkedList<T>();
        Dictionary<K, LinkedListNode<T>> _itemIndex = new Dictionary<K, LinkedListNode<T>>();

        public LinkedHashMap()
        {
        }

        #region IDictionary<K,T> Members

        public void Add(K key, T value)
        {
            LinkedListNode<T> node = new LinkedListNode<T>(value);
            _itemList.AddLast(node);
            _itemIndex.Add(key, node);
        }

        public bool ContainsKey(K key)
        {
            return _itemIndex.ContainsKey(key);
        }

        public ICollection<K> Keys
        {
            get { return _itemIndex.Keys; }
        }

        public bool Remove(K key)
        {
            LinkedListNode<T> node;
            if (_itemIndex.TryGetValue(key, out node))
            {
                _itemList.Remove(node);
                _itemIndex.Remove(key);

                return true;
            }

            return false;
        }

        public bool TryGetValue(K key, out T value)
        {
            LinkedListNode<T> node;
            if (_itemIndex.TryGetValue(key, out node))
            {
                value = node.Value;
                return true;
            }

            value = default(T);
            return false;
        }

        public ICollection<T> Values
        {
            get 
            {
                return _itemList;
            }
        }

        public T this[K key]
        {
            get
            {
                LinkedListNode<T> item = _itemIndex[key];
                return item.Value;
            }
        }

        #endregion

        #region ICollection<KeyValuePair<K,T>> Members

        public void Add(KeyValuePair<K, T> item)
        {
            this.Add(item.Key, item.Value);
        }

        public void Clear()
        {
            _itemIndex.Clear();
            _itemList.Clear();
        }

        public bool Contains(KeyValuePair<K, T> item)
        {
            return _itemIndex.ContainsKey(item.Key);
        }

        public int Count
        {
            get { return _itemList.Count; }
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public bool Remove(KeyValuePair<K, T> item)
        {
            return this.Remove(item.Key);
        }

        #endregion

        #region IEnumerable<T> Members

        public IEnumerator<T> GetEnumerator()
        {
            return _itemList.GetEnumerator();
        }

        #endregion

        #region IEnumerable Members

        System.Collections.IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        #endregion
    }
}
