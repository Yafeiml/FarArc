using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using DragablzDemo.Annotations;

namespace DragablzDemo
{
    public class CustomHeaderViewModel : INotifyPropertyChanged
    {
        private string _header;
        private bool _isSelected;

        public string Header
        {
            get { return _header; }
            set
            {
                if (value == _header) return;
                _header = value;
                OnPropertyChanged();
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (value.Equals(_isSelected)) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
