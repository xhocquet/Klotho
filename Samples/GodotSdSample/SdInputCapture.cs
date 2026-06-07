// Godot input capture for SdSample: WASD and arrow keys -> FP64 H/V in [-1,1]. Same namespace/signature
// as the Unity SdInputCapture so the copied SdSimulationCallbacks compiles unchanged.
using System;
using global::Godot;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Samples.SdSample
{
	public class SdInputCapture : IDisposable
	{
		public FP64 H { get; private set; }
		public FP64 V { get; private set; }

		public void CaptureInput()
		{
			float h = (Pressed(Key.D) || Pressed(Key.Right) ? 1f : 0f)
					- (Pressed(Key.A) || Pressed(Key.Left)  ? 1f : 0f);
			// V maps to world +Z (MovementSystem: velocity.z = V). The top-down camera's screen-up is
			// world -Z, so W/Up must produce -V to move "up" on screen.
			float v = (Pressed(Key.S) || Pressed(Key.Down)  ? 1f : 0f)
					- (Pressed(Key.W) || Pressed(Key.Up)    ? 1f : 0f);
			H = FP64.FromFloat(h);
			V = FP64.FromFloat(v);
		}

		private static bool Pressed(Key k) => Input.IsPhysicalKeyPressed(k);

		public void Enable() { }
		public void Disable() { }
		public void Dispose() { }
	}
}
