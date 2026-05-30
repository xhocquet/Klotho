using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.SdSample
{
    [CreateAssetMenu(menuName = "SdSample/EntityViewFactory", fileName = "SdEntityViewFactory")]
    public class SdEntityViewFactory : EntityViewFactory
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
