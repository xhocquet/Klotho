// Player view node. Inherits the adapter EntityViewNode; the mesh is supplied by player.tscn.
// On activation it tints the mesh by PlayerId so P1/P2 are visually distinct.
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Godot;

namespace xpTURN.Samples.P2pSample
{
	public partial class P2pPlayerView : EntityViewNode
	{
		public override void OnActivate(FrameRef frame)
		{
			var f = frame.Frame;
			if (f == null || !f.Has<PlayerComponent>(EntityRef)) return;

			int playerId = f.Get<PlayerComponent>(EntityRef).PlayerId;
			var mesh = GetNodeOrNull<MeshInstance3D>("Mesh");
			if (mesh == null) return;

			// P1 = blue, P2 = red.
			var color = playerId == 0 ? new Color(0.30f, 0.55f, 1.0f) : new Color(1.0f, 0.40f, 0.35f);
			mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = color };
		}
	}
}
