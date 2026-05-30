using System;
using UnityEngine;
using UnityEngine.InputSystem;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Samples.SdSample
{
    public class SdInputCapture : IDisposable
    {
        private readonly InputAction _move;

        public FP64 H { get; private set; }
        public FP64 V { get; private set; }

        public SdInputCapture()
        {
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                 .With("Up", "<Keyboard>/w")
                 .With("Down", "<Keyboard>/s")
                 .With("Left", "<Keyboard>/a")
                 .With("Right", "<Keyboard>/d");
            _move.AddCompositeBinding("2DVector")
                 .With("Up", "<Keyboard>/upArrow")
                 .With("Down", "<Keyboard>/downArrow")
                 .With("Left", "<Keyboard>/leftArrow")
                 .With("Right", "<Keyboard>/rightArrow");
        }

        public void CaptureInput()
        {
            Vector2 move = _move.ReadValue<Vector2>();
            H = FP64.FromFloat(Mathf.Clamp(move.x, -1f, 1f));
            V = FP64.FromFloat(Mathf.Clamp(move.y, -1f, 1f));
        }

        public void Enable() => _move.Enable();
        public void Disable() => _move.Disable();
        public void Dispose() => _move?.Dispose();
    }
}
