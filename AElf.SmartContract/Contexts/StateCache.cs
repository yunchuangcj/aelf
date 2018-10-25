﻿namespace AElf.SmartContract
{
    public class StateCache
    {
        private byte[] _currentValue;

        public StateCache(byte[] initialValue)
        {
            InitialValue = initialValue;
            _currentValue = initialValue;
        }

        public bool Dirty { get; private set; } = false;

        public byte[] InitialValue { get; }

        public byte[] CurrentValue
        {
            get => _currentValue;
            set
            {
                Dirty = true;
                _currentValue = value;
            }
        }

        public void SetValue(byte[] value)
        {
            Dirty = true;
            CurrentValue = value;
        }
    }
}