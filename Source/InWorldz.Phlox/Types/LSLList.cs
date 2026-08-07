using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenMetaverse;
using System.Collections;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// An LSL script list object. An immutable list.
    /// </summary>
    public class LSLList : IEquatable<LSLList>
    {
        public const int MEM_OVERHEAD = 16;

        private List<object> _members;
        private object[] _membersArray;
        private int _memorySize;

        public int MemorySize
        {
            get
            {
                return _memorySize + MEM_OVERHEAD;
            }
        }

        public List<object> Members
        {
            get
            {
                return _members;
            }
        }

        /// <summary>
        /// Backwards compat
        /// </summary>
        public object[] Data
        {
            get
            {
                return _membersArray;
            }
            
        }

        public int Length
        {
            get
            {
                return _members.Count;
            }
        }

        private void SetMembersArray()
        {
            _membersArray = _members.ToArray();
        }

        public LSLList()
        {
            _members = new List<object>(0);
            SetMembersArray();
            _memorySize = 0;
        }

        public LSLList(List<object> members)
        {
            this.MembersInit(members);
            CalcMemSize();
        }

        public LSLList(List<object> members, int memorySize)
        {
            this.MembersInit(members);
            _memorySize = memorySize;
            CheckMemorySize();
        }

        private void CheckMemorySize()
        {
            //TODO: this sucks, refactor it should reference max memory from somewhere else
            const int MAX_MEMORY = 0x20000;
            if (_memorySize > MAX_MEMORY)
            {
                throw new CheckException("Out of memory");
            }
        }

        public LSLList(LSLList other)
        {
            _members = other._members;
            _membersArray = other._membersArray;
            _memorySize = other._memorySize;
        }

        public LSLList(object member)
        {
            _members = new List<object>(1);
            _members.Add(member);
            SetMembersArray();
            CalcMemSize();
        }

        public LSLList(object[] members)
        {
            _members = new List<object>(members.Length);
            _members.AddRange(members);
            _membersArray = members;
            CalcMemSize();
        }

        private void CalcMemSize()
        {
            _memorySize = Util.MemoryCalc.CalcSizeOf(_members);
            CheckMemorySize();
        }

        private void MembersInit(List<object> members)
        {
            _members = members;
            SetMembersArray();
        }

        /// <summary>
        /// Prepends the given list to this list
        /// </summary>
        /// <param name="list"></param>
        /// <returns></returns>
        public LSLList Prepend(LSLList list)
        {
            List<object> newList = new List<object>(list.Members);
            newList.AddRange(_members);

            return new LSLList(newList, this._memorySize + list._memorySize);
        }

        /// <summary>
        /// Appends the given list to this list
        /// </summary>
        /// <param name="list"></param>
        /// <returns></returns>
        public LSLList Append(LSLList list)
        {
            List<object> newList = new List<object>(_members);
            newList.AddRange(list.Members);

            return new LSLList(newList, this._memorySize + list._memorySize);
        }

        /// <summary>
        /// Same as append
        /// </summary>
        /// <param name="l"></param>
        /// <param name="r"></param>
        /// <returns></returns>
        public static LSLList operator +(LSLList l, LSLList r)
        {
            return l.Append(r);
        }

        /// <summary>
        /// Prepends the given object to this list
        /// </summary>
        /// <param name="obj"></param>
        /// <returns>The new list with the object prepended</returns>
        public LSLList Prepend(object obj)
        {
            if (obj is LSLList)
            {
                return this.Prepend((LSLList)obj);
            }
            else
            {
                List<object> newList = new List<object>(_members.Count + 1);
                newList.Add(obj);
                newList.AddRange(_members);

                return new LSLList(newList, _memorySize + Util.MemoryCalc.CalcSizeOf(obj));
            }
        }

        /// <summary>
        /// Appends the given object onto this list
        /// </summary>
        /// <param name="obj"></param>
        /// <returns>The new list with the object appended</returns>
        public LSLList Append(object obj)
        {
            if (obj is LSLList)
            {
                return this.Append((LSLList)obj);
            }
            else
            {
                List<object> newList = new List<object>(_members.Count + 1);
                newList.AddRange(_members);
                newList.Add(obj);

                return new LSLList(newList, _memorySize + Util.MemoryCalc.CalcSizeOf(obj));
            }
        }

        public override string ToString()
        {
            StringBuilder str = new StringBuilder("[");

            for (int i = 0; i < _members.Count; ++i)
            {
                if (i != 0) str.Append(",");
                str.Append(_members[i]);
            }

            str.Append("]");

            return str.ToString();
        }

        private bool IsOutOfRange(int startIndex, int endIndex)
        {
            if (startIndex < 0 || endIndex < 0)
            {
                return true;
            }

            if (startIndex > endIndex)
            {
                return true;
            }

            if (startIndex >= _members.Count)
            {
                return true;
            }

            if (endIndex >= _members.Count)
            {
                return true;
            }

            return false;
        }

        public LSLList GetSublist(int start, int end)
        {
            object[] ret;

            // Take care of neg start or end's
            // NOTE that either index may still be negative after
            // adding the length, so we must take additional
            // measures to protect against this. Note also that
            // after normalisation the negative indices are no
            // longer relative to the end of the list.

            if (start < 0)
            {
                start = _members.Count + start;
            }

            if (end < 0)
            {
                end = _members.Count + end;
            }

            // The conventional case is start <= end
            // NOTE that the case of an empty list is
            // dealt with by the initial test. Start
            // less than end is taken to be the most
            // common case.

            if (start <= end)
            {

                // Start sublist beyond length
                // Also deals with start AND end still negative
                if (start >= _members.Count || end < 0)
                {
                    return new LSLList();
                }

                // Sublist extends beyond the end of the supplied list
                if (end >= _members.Count)
                {
                    end = _members.Count - 1;
                }

                // Sublist still starts before the beginning of the list
                if (start < 0)
                {
                    start = 0;
                }

                ret = new object[end - start + 1];
                _members.CopyTo(start, ret, 0, end - start + 1);
                
                return new LSLList(ret);

            }

            // Deal with the segmented case: 0->end + start->EOL

            else
            {

                LSLList result = null;

                // If end is negative, then prefix list is empty
                if (end < 0)
                {
                    result = new LSLList();
                    // If start is still negative, then the whole of
                    // the existing list is returned. This case is
                    // only admitted if end is also still negative.
                    if (start < 0)
                    {
                        return this;
                    }

                }
                else
                {
                    result = GetSublist(0, end);
                }

                // If start is outside of list, then just return
                // the prefix, whatever it is.
                if (start >= _members.Count)
                {
                    return result;
                }

                return result + GetSublist(start, Data.Length);

            }
        }

        public Vector3 GetVector3Item(int index)
        {
            if (index >= _members.Count || index < 0)
            {
                return Vector3.Zero;
            }

            if (_members[index] is Vector3)
            {
                return (Vector3)_members[index];
            }
            else if (_members[index] is string)
            {
                Vector3 ret;
                if (Vector3.TryParse((string)_members[index], out ret))
                {
                    return ret;
                }
            }
            
            return Vector3.Zero;
        }

        public Quaternion GetQuaternionItem(int index)
        {
            if (index >= _members.Count || index < 0)
            {
                return Quaternion.Identity;
            }

            if (_members[index] is Quaternion)
            {
                return (Quaternion)_members[index];
            }
            else if (_members[index] is string)
            {
                Quaternion ret;
                if (Quaternion.TryParse((string)_members[index], out ret))
                {
                    return ret;
                }
            }

            return Quaternion.Identity;
        }

        public float GetLSLFloatItem(int index)
        {
            if (index >= _members.Count || index < 0)
            {
                return 0.0f;
            }

            if (_members[index] is float)
            {
                return (float)_members[index];
            }
            else if (_members[index] is int)
            {
                return (float)(int)_members[index];
            }
            else if (_members[index] is string)
            {
                return Util.Encoding.CastToFloat((string)_members[index]);
            }
            
            return 0.0f;
        }

        public int GetLSLIntegerItem(int index)
        {
            if (index >= _members.Count || index < 0)
            {
                return 0;
            }

            if (_members[index] is int)
            {
                return (int)_members[index];
            }
            else if (_members[index] is float)
            {
                return (int)(float)_members[index];
            }
            else if (_members[index] is string)
            {
                return Util.Encoding.CastToInt((string)_members[index]);
            }
            
            return 0;
        }

        public string GetLSLStringItem(int index)
        {
            if (index >= _members.Count || index < 0)
            {
                return string.Empty;
            }

            if (_members[index] is string)
            {
                return (string)_members[index];
            }
            else if (_members[index] is float)
            {
                return Util.Encoding.FloatToStringWith6FractionalDigits((float)_members[index]);
            }
            else if (_members[index] is Vector3)
            {
                return Util.Encoding.Vector3ToStringWith6FractionalDigits((Vector3)_members[index]);
            }
            else if (_members[index] is Quaternion)
            {
                return Util.Encoding.QuaternionToStringWith6FractionalDigits((Quaternion)_members[index]);
            }
            else
            {
                return _members[index].ToString();
            }
        }

        private static float QuatMag(Quaternion q)
        {
            return (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        }

        private static int compare(object left, object right, int ascending)
        {
            if (!left.GetType().Equals(right.GetType()))
            {
                // unequal types are always "equal" for comparison purposes.
                // this way, the bubble sort will never swap them, and we'll
                // get that feathered effect we're looking for
                return 0;
            }

            int ret = 0;

            if (left is string)
            {
                string l = (string)left;
                string r = (string)right;
                ret = String.CompareOrdinal(l, r);
            }
            else if (left is int)
            {
                int l = (int)left;
                int r = (int)right;
                ret = Math.Sign(l - r);
            }
            else if (left is float)
            {
                float l = (float)left;
                float r = (float)right;
                ret = Math.Sign(l - r);
            }
            else if (left is Vector3)
            {
                Vector3 l = (Vector3)left;
                Vector3 r = (Vector3)right;
                ret = Math.Sign(Vector3.Mag(l) - Vector3.Mag(r));
            }
            else if (left is Quaternion)
            {
                Quaternion l = (Quaternion)left;
                Quaternion r = (Quaternion)right;
                ret = Math.Sign(QuatMag(l) - QuatMag(r));
            }

            if (ascending == 0)
            {
                ret = 0 - ret;
            }

            return ret;
        }

        class HomogeneousComparer : IComparer
        {
            public HomogeneousComparer()
            {
            }

            public int Compare(object lhs, object rhs)
            {
                return compare(lhs, rhs, 1);
            }
        }

        public LSLList Sort(int stride, int ascending)
        {
            if (Data.Length == 0)
                return new LSLList(); // Don't even bother

            object[] ret = new object[Data.Length];
            Array.Copy(Data, 0, ret, 0, Data.Length);

            if (stride <= 0)
            {
                stride = 1;
            }

            // we can optimize here in the case where stride == 1 and the list
            // consists of homogeneous types

            if (stride == 1)
            {
                bool homogeneous = true;
                int index;
                for (index = 1; index < Data.Length; index++)
                {
                    if (!Data[0].GetType().Equals(Data[index].GetType()))
                    {
                        homogeneous = false;
                        break;
                    }
                }

                if (homogeneous)
                {
                    Array.Sort(ret, new HomogeneousComparer());
                    if (ascending == 0)
                    {
                        Array.Reverse(ret);
                    }
                    return new LSLList(ret);
                }
            }

            // Because of the desired type specific feathered sorting behavior
            // requried by the spec, we MUST use a non-optimized bubble sort here.
            // Anything else will give you the incorrect behavior.

            // begin bubble sort...
            int i;
            int j;
            int k;
            int n = Data.Length;

            for (i = 0; i < (n - stride); i += stride)
            {
                for (j = i + stride; j < n; j += stride)
                {
                    if (compare(ret[i], ret[j], ascending) > 0)
                    {
                        for (k = 0; k < stride; k++)
                        {
                            object tmp = ret[i + k];
                            ret[i + k] = ret[j + k];
                            ret[j + k] = tmp;
                        }
                    }
                }
            }

            // end bubble sort

            return new LSLList(ret);
        }

        public static LSLList ToFloatList(LSLList src)
        {
            List<object> ret = new List<object>();
            float entry = 0;
            for (int i = 0; i < src.Data.Length; i++)
            {
                if (float.TryParse(src.Data[i].ToString(), out entry))
                {
                    ret.Add(entry);
                }
            }
            return new LSLList(ret, ret.Count * Util.MemoryCalc.CalcSizeOf(entry));
        }

        public class AlphaCompare : IComparer
        {
            int IComparer.Compare(object x, object y)
            {
                return string.Compare(x.ToString(), y.ToString());
            }
        }

        public class NumericComparer : IComparer
        {
            int IComparer.Compare(object x, object y)
            {
                float a;
                float b;
                if (!float.TryParse(x.ToString(), out a))
                {
                    a = 0.0f;
                }
                if (!float.TryParse(y.ToString(), out b))
                {
                    b = 0.0f;
                }
                if (a < b)
                {
                    return -1;
                }
                else if (a == b)
                {
                    return 0;
                }
                else
                {
                    return 1;
                }
            }
        }

        public float Sum()
        {
            float sum = 0;
            float entry;
            for (int i = 0; i < Data.Length; i++)
            {
                if (float.TryParse(Data[i].ToString(), out entry))
                {
                    sum = sum + entry;
                }
            }
            return sum;
        }

        public float SumSqrs()
        {
            float sum = 0;
            float entry;
            for (int i = 0; i < Data.Length; i++)
            {
                if (float.TryParse(Data[i].ToString(), out entry))
                {
                    sum = sum + (float)Math.Pow(entry, 2);
                }
            }
            return sum;
        }

        public float Min()
        {
            float minimum = float.PositiveInfinity;
            float entry;
            for (int i = 0; i < Data.Length; i++)
            {
                if (float.TryParse(Data[i].ToString(), out entry))
                {
                    if (entry < minimum) minimum = entry;
                }
            }
            return minimum;
        }

        public float Max()
        {
            float maximum = float.NegativeInfinity;
            float entry;
            for (int i = 0; i < Data.Length; i++)
            {
                if (float.TryParse(Data[i].ToString(), out entry))
                {
                    if (entry > maximum) maximum = entry;
                }
            }
            return maximum;
        }

        public float Range()
        {
            return (this.Max() / this.Min());
        }

        public int NumericLength()
        {
            int count = 0;
            float entry;
            for (int i = 0; i < Data.Length; i++)
            {
                if (float.TryParse(Data[i].ToString(), out entry))
                {
                    count++;
                }
            }
            return count;
        }

        public float Mean()
        {
            return (this.Sum() / this.NumericLength());
        }

        public void NumericSort()
        {
            IComparer Numeric = new NumericComparer();
            Array.Sort(Data, Numeric);
        }

        public void AlphaSort()
        {
            IComparer Alpha = new AlphaCompare();
            Array.Sort(Data, Alpha);
        }

        public float Median()
        {
            return Qi(0.5f);
        }

        public float GeometricMean()
        {
            float ret = 1.0f;
            LSLList nums = ToFloatList(this);
            for (int i = 0; i < nums.Data.Length; i++)
            {
                ret *= (float)nums.Data[i];
            }
            return (float)Math.Exp(Math.Log(ret) / (float)nums.Data.Length);
        }

        public float HarmonicMean()
        {
            float ret = 0.0f;
            LSLList nums = ToFloatList(this);
            for (int i = 0; i < nums.Data.Length; i++)
            {
                ret += 1.0f / (float)nums.Data[i];
            }
            return ((float)nums.Data.Length / ret);
        }

        public float Variance()
        {
            float s = 0;
            LSLList num = ToFloatList(this);
            for (int i = 0; i < num.Data.Length; i++)
            {
                s += (float)Math.Pow((float)num.Data[i], 2);
            }
            return (s - num.Data.Length * (float)Math.Pow(num.Mean(), 2)) / (num.Data.Length - 1);
        }

        public float StdDev()
        {
            return (float)Math.Sqrt(this.Variance());
        }

        public float Qi(float i)
        {
            LSLList j = this;
            j.NumericSort();

            if (Math.Ceiling(this.Length * i) == this.Length * i)
            {
                return (float)((float)j.Data[(int)(this.Length * i - 1)] + (float)j.Data[(int)(this.Length * i)]) / 2;
            }
            else
            {
                return (float)j.Data[((int)(Math.Ceiling(this.Length * i))) - 1];
            }
        }

        public VarType GetItemType(int index)
        {
            if (index >= _members.Count || index < 0)
            {
                return VarType.Void;
            }

            return RuntimeMirror.VarTypeFromRuntimeType(_members[index].GetType());
        }

        public LSLList DeleteSublist(int start, int end)
        {
            // Not an easy one
            // If start <= end, remove that part
            // if either is negative, count from the end of the array
            // if the resulting start > end, remove all BUT that part

            object[] ret;

            if (start < 0)
                start = Data.Length + start;

            if (start < 0)
                start = 0;

            if (end < 0)
                end = Data.Length + end;
            if (end < 0)
                end = 0;

            if (start > end)
            {
                if (end >= Data.Length)
                    return new LSLList(new Object[0]);

                if (start >= Data.Length)
                    start = Data.Length - 1;

                return GetSublist(end, start);
            }

            // start >= 0 && end >= 0 here
            if (start >= Data.Length)
            {
                ret = new Object[Data.Length];
                Array.Copy(Data, 0, ret, 0, Data.Length);

                return new LSLList(ret);
            }

            if (end >= Data.Length)
                end = Data.Length - 1;

            // now, this makes the math easier
            int remove = end + 1 - start;

            ret = new Object[Data.Length - remove];
            if (ret.Length == 0)
                return new LSLList(ret);

            int src;
            int dest = 0;

            for (src = 0; src < Data.Length; src++)
            {
                if (src < start || src > end)
                    ret[dest++] = Data[src];
            }

            return new LSLList(ret);
        }

        #region IEquatable<LSLList> Members

        public bool Equals(LSLList other)
        {
            if (other.Length != this.Length)
            {
                return false;
            }

            for (int i = 0; i < _members.Count; i++)
            {
                if (!this.Members[i].Equals(other.Members[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            LSLList other = obj as LSLList;
            if (other == null)
            {
                return false;
            }

            return this.Equals(other);
        }

        #endregion
    }
}
