using System;
using System.Collections.Generic;

namespace Dragablz.Core
{
    internal class FuncComparer<TObject> : IComparer<TObject>
    {
        private readonly Func<TObject, TObject, int> _comparer;

        public FuncComparer(Func<TObject, TObject, int> comparer)
        {
            _comparer = comparer ?? throw new ArgumentNullException("comparer");
        }

        public int Compare(TObject? x, TObject? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            return _comparer(x, y);
        }
    }
}
