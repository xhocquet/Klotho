using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// ORCA half-plane.
    /// </summary>
    [Serializable]
    public struct FPOrcaLine
    {
        public FPVector2 point;
        public FPVector2 direction;
    }

    /// <summary>
    /// ORCA (Optimal Reciprocal Collision Avoidance) avoidance system.
    /// Agent-to-agent ORCA half-planes + 2D linear program solver.
    /// FP64 deterministic implementation.
    /// </summary>
    public class FPNavAvoidance
    {
        public const int MAX_ORCA_LINES = 64;
        public const int MAX_NEIGHBORS = 16;

        /// <summary>
        /// Neighbor search radius.
        /// </summary>
        public FP64 NeighborDist;

        /// <summary>
        /// Agent-to-agent time horizon.
        /// </summary>
        public FP64 TimeHorizon;

        /// <summary>
        /// Static obstacle time horizon.
        /// </summary>
        public FP64 TimeHorizonObst;

        // Pre-allocated buffers
        private readonly FPOrcaLine[] _orcaLines;
        private int _orcaLineCount;

        // Neighbor selection buffers (avoid per-frame allocation)
        private readonly FP64[] _neighborDistSqr = new FP64[MAX_NEIGHBORS];
        private readonly int[] _neighborIndices = new int[MAX_NEIGHBORS];
        private int _neighborCount;

        private static readonly FP64 EPSILON = FP64.FromRaw(100);

        public FPOrcaLine[] DebugOrcaLines => _orcaLines;
        public int DebugOrcaLineCount => _orcaLineCount;

        public FPNavAvoidance()
        {
            NeighborDist = FP64.FromInt(5);
            TimeHorizon = FP64.FromInt(3);
            TimeHorizonObst = FP64.FromInt(1);
            _orcaLines = new FPOrcaLine[MAX_ORCA_LINES];
            _orcaLineCount = 0;
        }

        /// Maintains a sorted buffer of the MAX_NEIGHBORS closest candidates.
        private void InsertNeighbor(int index, FP64 distSqr)
        {
            if (_neighborCount < MAX_NEIGHBORS)
            {
                // Buffer not full — insert in sorted position
                int pos = _neighborCount;
                while (pos > 0 && distSqr < _neighborDistSqr[pos - 1])
                {
                    _neighborDistSqr[pos] = _neighborDistSqr[pos - 1];
                    _neighborIndices[pos] = _neighborIndices[pos - 1];
                    pos--;
                }
                _neighborDistSqr[pos] = distSqr;
                _neighborIndices[pos] = index;
                _neighborCount++;
            }
            else if (distSqr < _neighborDistSqr[_neighborCount - 1])
            {
                // Closer than the farthest kept neighbor — replace and shift down
                int pos = _neighborCount - 1;
                while (pos > 0 && distSqr < _neighborDistSqr[pos - 1])
                {
                    _neighborDistSqr[pos] = _neighborDistSqr[pos - 1];
                    _neighborIndices[pos] = _neighborIndices[pos - 1];
                    pos--;
                }
                _neighborDistSqr[pos] = distSqr;
                _neighborIndices[pos] = index;
            }
        }

        /// <summary>
        /// Computes the ORCA half-plane between agents using FP64 fixed-point arithmetic.
        /// </summary>
        private static bool ComputeAgentOrcaLine(FPVector2 agentVelocity, FPVector2 relPos, FPVector2 relVel,
            FP64 combinedRadius, FP64 timeHorizon, FP64 dt, out FPOrcaLine line)
        {
            line = default;

            FP64 distSqr = relPos.sqrMagnitude;
            FP64 combinedRadiusSqr = combinedRadius * combinedRadius;
            FP64 invTimeHorizon = FP64.One / timeHorizon;

            FPVector2 u;

            if (distSqr > combinedRadiusSqr)
            {
                // No collision
                FPVector2 w = relVel - relPos * invTimeHorizon;
                FP64 wLenSqr = w.sqrMagnitude;
                FP64 dotProduct = FPVector2.Dot(w, relPos);

                if (dotProduct < FP64.Zero && dotProduct * dotProduct > combinedRadiusSqr * wLenSqr)
                {
                    // Project onto cutoff circle
                    FP64 wLen = FP64.Sqrt(wLenSqr);
                    if (wLen <= EPSILON)
                        return false;

                    FPVector2 unitW = w / wLen;
                    line.direction = new FPVector2(unitW.y, -unitW.x);
                    u = unitW * (combinedRadius * invTimeHorizon - wLen);
                }
                else
                {
                    // Project onto cone legs
                    FP64 leg = FP64.Sqrt(distSqr - combinedRadiusSqr);
                    if (leg <= EPSILON)
                        return false;

                    if (FPVector2.Cross(relPos, w) > FP64.Zero)
                    {
                        // Left leg
                        line.direction = new FPVector2(
                            relPos.x * leg - relPos.y * combinedRadius,
                            relPos.x * combinedRadius + relPos.y * leg) / distSqr;
                    }
                    else
                    {
                        // Right leg
                        line.direction = -new FPVector2(
                            relPos.x * leg + relPos.y * combinedRadius,
                            -relPos.x * combinedRadius + relPos.y * leg) / distSqr;
                    }

                    FP64 dotVelDir = FPVector2.Dot(relVel, line.direction);
                    u = line.direction * dotVelDir - relVel;
                }
            }
            else
            {
                // Already colliding → separate immediately
                FP64 invDt = FP64.One / dt;
                FPVector2 w = relVel - relPos * invDt;
                FP64 wLen = w.magnitude;

                if (wLen <= EPSILON)
                {
                    // If w is zero, separate along relPos direction
                    FP64 dist = FP64.Sqrt(distSqr);
                    FPVector2 unitRelPos = dist > EPSILON
                        ? relPos / dist
                        : new FPVector2(FP64.One, FP64.Zero);
                    line.direction = new FPVector2(-unitRelPos.y, unitRelPos.x);
                    u = -unitRelPos * (combinedRadius - dist);
                }
                else
                {
                    FPVector2 unitW = w / wLen;
                    line.direction = new FPVector2(unitW.y, -unitW.x);
                    u = unitW * (combinedRadius * invDt - wLen);
                }
            }

            // ORCA: shared responsibility (1/2 each)
            line.point = agentVelocity + u * FP64.Half;
            return true;
        }

        /// <summary>
        /// Computes the ORCA avoidance velocity based on NavAgentComponent.
        /// </summary>
        public FPVector2 ComputeNewVelocity(int agentIdx, ref Frame frame, EntityRef[] entities, int entityCount, FP64 dt)
        {
            ref var agent = ref frame.Get<NavAgentComponent>(entities[agentIdx]);
            _orcaLineCount = 0;

            FP64 neighborDistSqrMax = NeighborDist * NeighborDist;
            FPVector2 agentPosXZ = agent.Position.ToXZ();

            // Select up to MAX_NEIGHBORS closest neighbors
            _neighborCount = 0;
            for (int i = 0; i < entityCount; i++)
            {
                if (i == agentIdx)
                    continue;

                ref var other = ref frame.Get<NavAgentComponent>(entities[i]);
                FP64 distSqr = (other.Position.ToXZ() - agentPosXZ).sqrMagnitude;

                if (distSqr > neighborDistSqrMax)
                    continue;

                InsertNeighbor(i, distSqr);
            }

            // Build ORCA lines from selected neighbors
            for (int n = 0; n < _neighborCount && _orcaLineCount < MAX_ORCA_LINES; n++)
            {
                int i = _neighborIndices[n];
                ref var other = ref frame.Get<NavAgentComponent>(entities[i]);

                FP64 combinedRadius = agent.Radius + other.Radius;
                FPVector2 relPos = other.Position.ToXZ() - agentPosXZ;
                if (relPos.sqrMagnitude <= EPSILON)
                {
                    FP64 fallbackDistance = combinedRadius * FP64.FromDouble(0.9);
                    relPos = i < agentIdx
                        ? new FPVector2(-fallbackDistance, FP64.Zero)
                        : new FPVector2(fallbackDistance, FP64.Zero);
                }

                FPVector2 relVel = agent.Velocity - other.Velocity;

                if (ComputeAgentOrcaLine(agent.Velocity, relPos, relVel, combinedRadius, TimeHorizon, dt,
                    out FPOrcaLine line))
                {
                    _orcaLines[_orcaLineCount++] = line;
                }
            }

            return LinearProgram2D(agent.DesiredVelocity, agent.Speed);
        }

        /// <summary>
        /// 2D linear program: finds the velocity closest to desiredVelocity that satisfies all ORCA half-planes.
        /// Uses incremental constraint addition.
        /// </summary>
        private FPVector2 LinearProgram2D(FPVector2 preferredVelocity, FP64 maxSpeed)
        {
            FPVector2 result = preferredVelocity;

            // Max speed constraint (circular)
            FP64 maxSpeedSqr = maxSpeed * maxSpeed;
            if (result.sqrMagnitude > maxSpeedSqr)
            {
                result = result.normalized * maxSpeed;
            }

            // Add half-plane constraints one by one
            for (int i = 0; i < _orcaLineCount; i++)
            {
                // Left normal of direction = Perpendicular
                // det < 0 → result violates the half-plane
                FP64 det = FPVector2.Cross(_orcaLines[i].direction, result - _orcaLines[i].point);

                if (det < FP64.Zero)
                {
                    FPVector2 newResult = ProjectOntoOrcaLine(i, result, maxSpeed);
                    result = newResult;
                }
            }

            return result;
        }

        /// <summary>
        /// Projects onto an ORCA line while satisfying all previous constraints and max speed.
        /// RVO2 linearProgram1 approach: clamps to [tLeft, tRight] range.
        /// </summary>
        private FPVector2 ProjectOntoOrcaLine(int lineIdx, FPVector2 result, FP64 maxSpeed)
        {
            ref FPOrcaLine line = ref _orcaLines[lineIdx];

            // Intersection range [tLeft, tRight] of the line with the max-speed circle
            FP64 dotProduct = FPVector2.Dot(line.point, line.direction);
            FP64 discriminant = dotProduct * dotProduct
                + maxSpeed * maxSpeed - line.point.sqrMagnitude;

            if (discriminant < FP64.Zero)
            {
                // Line does not intersect the speed circle → no valid projection
                return result;
            }

            FP64 sqrtDisc = FP64.Sqrt(discriminant);
            FP64 tLeft = -dotProduct - sqrtDisc;
            FP64 tRight = -dotProduct + sqrtDisc;

            // Narrow [tLeft, tRight] using previous constraints (0..lineIdx-1)
            for (int j = 0; j < lineIdx; j++)
            {
                FP64 denom = FPVector2.Cross(line.direction, _orcaLines[j].direction);
                FP64 numer = FPVector2.Cross(_orcaLines[j].direction,
                    line.point - _orcaLines[j].point);

                if (FP64.Abs(denom) <= EPSILON)
                {
                    // Parallel constraints — ignore if same direction, infeasible if opposite
                    if (numer < FP64.Zero)
                        return result;
                    continue;
                }

                FP64 tLine = numer / denom;

                if (denom >= FP64.Zero)
                {
                    // Constraint j narrows the right boundary
                    if (tLine < tRight)
                        tRight = tLine;
                }
                else
                {
                    // Constraint j narrows the left boundary
                    if (tLine > tLeft)
                        tLeft = tLine;
                }

                if (tLeft > tRight)
                    return result;
            }

            // Clamp the projection of result onto line to [tLeft, tRight]
            FP64 t = FPVector2.Dot(line.direction, result - line.point);

            if (t < tLeft)
                t = tLeft;
            else if (t > tRight)
                t = tRight;

            return line.point + line.direction * t;
        }

    }
}
