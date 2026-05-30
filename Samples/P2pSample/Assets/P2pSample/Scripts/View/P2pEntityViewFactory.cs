using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.P2pSample
{
    [CreateAssetMenu(menuName = "P2pSample/EntityViewFactory", fileName = "P2pEntityViewFactory")]
    public class P2pEntityViewFactory : EntityViewFactory
    {
        [SerializeField] private GameObject _playerPrefab;

        protected override bool ShouldRender(Frame frame, EntityRef entity)
        {
            return frame.Has<PlayerComponent>(entity);
        }

        protected override GameObject ResolvePrefab(Frame frame, EntityRef entity)
        {
            return frame.Has<PlayerComponent>(entity) ? _playerPrefab : null;
        }
    }
}
