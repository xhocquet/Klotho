// Maps player entities to the player PackedScene (P2pPlayerView root). Mirrors the Unity factory.
using global::Godot;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Godot;

namespace xpTURN.Samples.P2pSample
{
	public class P2pEntityViewFactory : EntityViewFactory
	{
		private readonly PackedScene _playerScene;

		public P2pEntityViewFactory(PackedScene playerScene)
		{
			_playerScene = playerScene;
		}

		protected override bool ShouldRender(Frame frame, EntityRef entity)
			=> frame.Has<PlayerComponent>(entity);

		protected override PackedScene ResolvePrefab(Frame frame, EntityRef entity)
			=> frame.Has<PlayerComponent>(entity) ? _playerScene : null;
	}
}
