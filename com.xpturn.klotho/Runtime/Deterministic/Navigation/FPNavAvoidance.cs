using System;
using System.Collections.Generic;
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

        // Projected constraint buffer for LinearProgram3 (RVO2 projLines) — zero-GC
        private readonly FPOrcaLine[] _projLines = new FPOrcaLine[MAX_ORCA_LINES];
        private int _infeasibleCount;

        private static readonly FP64 EPSILON = FP64.FromRaw(100);
        private static readonly FP64 COINCIDENT_FACTOR = FP64.FromDouble(0.9);

        // Parallel threshold for the projLines path (LP3 bisector gate + inner LP narrowing).
        // Wider than EPSILON by design: keeps bisector intersection quotients in the exact
        // FP64 Q31.32 range (overflow bound: P·(1 + 2/eps) < ~46,340, worst project P = 36.25).
        private static readonly FP64 LP3_PARALLEL_EPSILON = FP64.FromDouble(0.01);

        public FPOrcaLine[] DebugOrcaLines => _orcaLines;
        public int DebugOrcaLineCount => _orcaLineCount;
        public int DebugInfeasibleCount => _infeasibleCount;

        public FPNavAvoidance()
        {
            NeighborDist = FP64.FromInt(5);
            TimeHorizon = FP64.FromInt(3);
            TimeHorizonObst = FP64.FromInt(1);
            _orcaLines = new FPOrcaLine[MAX_ORCA_LINES];
            _orcaLineCount = 0;
        }

        /// <summary>
        /// Maintains a sorted buffer of the MAX_NEIGHBORS closest candidates.
        /// </summary>
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
                    FP64 fallbackDistance = combinedRadius * COINCIDENT_FACTOR;
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

            FPVector2 result = FPVector2.Zero;
            int lineFail = LinearProgram2D(_orcaLines, _orcaLineCount, agent.Speed,
                agent.DesiredVelocity, false, EPSILON, ref result);
            if (lineFail < _orcaLineCount)
            {
                _infeasibleCount++;
                LinearProgram3(lineFail, agent.Speed, ref result);
            }
            return result;
        }

        /// <summary>
        /// Grid-accelerated overload: accepts pre-filtered neighbors instead of scanning all entities.
        /// </summary>
        public FPVector2 ComputeNewVelocity(EntityRef agentEntity, ref Frame frame,
            List<EntityRef> neighbors, FP64 dt)
        {
            ref var agent = ref frame.Get<NavAgentComponent>(agentEntity);
            _orcaLineCount = 0;

            FPVector2 agentPosXZ = agent.Position.ToXZ();

            // Select up to MAX_NEIGHBORS closest neighbors
            _neighborCount = 0;
            for (int i = 0; i < neighbors.Count; i++)
            {
                if (neighbors[i].Index == agentEntity.Index)
                    continue;

                ref var other = ref frame.Get<NavAgentComponent>(neighbors[i]);
                FP64 distSqr = (other.Position.ToXZ() - agentPosXZ).sqrMagnitude;

                InsertNeighbor(i, distSqr);
            }

            // Build ORCA lines from selected neighbors
            for (int n = 0; n < _neighborCount && _orcaLineCount < MAX_ORCA_LINES; n++)
            {
                int i = _neighborIndices[n];
                ref var other = ref frame.Get<NavAgentComponent>(neighbors[i]);

                FP64 combinedRadius = agent.Radius + other.Radius;
                FPVector2 relPos = other.Position.ToXZ() - agentPosXZ;
                if (relPos.sqrMagnitude <= EPSILON)
                {
                    FP64 fallbackDistance = combinedRadius * COINCIDENT_FACTOR;
                    relPos = neighbors[i].Index < agentEntity.Index
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

            FPVector2 gridResult = FPVector2.Zero;
            int gridLineFail = LinearProgram2D(_orcaLines, _orcaLineCount, agent.Speed,
                agent.DesiredVelocity, false, EPSILON, ref gridResult);
            if (gridLineFail < _orcaLineCount)
            {
                _infeasibleCount++;
                LinearProgram3(gridLineFail, agent.Speed, ref gridResult);
            }
            return gridResult;
        }

        /// <summary>
        /// 2D linear program (RVO2 linearProgram2): finds the velocity closest to optVelocity
        /// that satisfies all half-planes, using incremental constraint addition.
        /// Returns the index of the first failing line, or count when all constraints hold.
        /// </summary>
        private static int LinearProgram2D(FPOrcaLine[] lines, int count, FP64 maxSpeed,
            FPVector2 optVelocity, bool directionOpt, FP64 parallelEpsilon, ref FPVector2 result)
        {
            if (directionOpt)
            {
                // optVelocity is a unit direction — maximize along it
                result = optVelocity * maxSpeed;
            }
            else
            {
                result = optVelocity;

                // Max speed constraint (circular)
                FP64 maxSpeedSqr = maxSpeed * maxSpeed;
                if (result.sqrMagnitude > maxSpeedSqr)
                {
                    result = result.normalized * maxSpeed;
                }
            }

            // Add half-plane constraints one by one
            for (int i = 0; i < count; i++)
            {
                // Left normal of direction = Perpendicular
                // det < 0 → result violates the half-plane
                FP64 det = FPVector2.Cross(lines[i].direction, result - lines[i].point);

                if (det < FP64.Zero)
                {
                    FPVector2 tempResult = result;
                    if (!ProjectOntoOrcaLine(lines, i, maxSpeed, optVelocity, directionOpt,
                        parallelEpsilon, ref result))
                    {
                        result = tempResult;
                        return i;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Projects onto an ORCA line while satisfying all previous constraints and max speed.
        /// RVO2 linearProgram1 approach: clamps to [tLeft, tRight] range.
        /// Returns false when the constraint is infeasible (result left unchanged).
        /// </summary>
        private static bool ProjectOntoOrcaLine(FPOrcaLine[] lines, int lineIdx, FP64 maxSpeed,
            FPVector2 optVelocity, bool directionOpt, FP64 parallelEpsilon, ref FPVector2 result)
        {
            ref FPOrcaLine line = ref lines[lineIdx];

            // Intersection range [tLeft, tRight] of the line with the max-speed circle
            FP64 dotProduct = FPVector2.Dot(line.point, line.direction);
            FP64 discriminant = dotProduct * dotProduct
                + maxSpeed * maxSpeed - line.point.sqrMagnitude;

            if (discriminant < FP64.Zero)
            {
                // Line does not intersect the speed circle → no valid projection
                return false;
            }

            FP64 sqrtDisc = FP64.Sqrt(discriminant);
            FP64 tLeft = -dotProduct - sqrtDisc;
            FP64 tRight = -dotProduct + sqrtDisc;

            // Narrow [tLeft, tRight] using previous constraints (0..lineIdx-1)
            for (int j = 0; j < lineIdx; j++)
            {
                FP64 denom = FPVector2.Cross(line.direction, lines[j].direction);
                FP64 numer = FPVector2.Cross(lines[j].direction,
                    line.point - lines[j].point);

                if (FP64.Abs(denom) <= parallelEpsilon)
                {
                    // Parallel constraints — ignore if same direction, infeasible if opposite
                    if (numer < FP64.Zero)
                        return false;
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
                    return false;
            }

            if (directionOpt)
            {
                // Maximize along the optVelocity direction (RVO2 linearProgram1 directionOpt)
                result = FPVector2.Dot(optVelocity, line.direction) > FP64.Zero
                    ? line.point + line.direction * tRight
                    : line.point + line.direction * tLeft;
            }
            else
            {
                // Clamp the projection of result onto line to [tLeft, tRight]
                FP64 t = FPVector2.Dot(line.direction, result - line.point);

                if (t < tLeft)
                    t = tLeft;
                else if (t > tRight)
                    t = tRight;

                result = line.point + line.direction * t;
            }

            return true;
        }

        /// <summary>
        /// RVO2 linearProgram3: when the 2D program is infeasible, relaxes to the velocity
        /// that minimizes the maximum constraint violation (progressive bisector projection).
        /// </summary>
        private void LinearProgram3(int beginLine, FP64 maxSpeed, ref FPVector2 result)
        {
            FP64 distance = FP64.Zero;

            for (int i = beginLine; i < _orcaLineCount; i++)
            {
                // Skip lines whose violation is within the current maximum
                if (FPVector2.Cross(_orcaLines[i].direction, _orcaLines[i].point - result) <= distance)
                    continue;

                int projCount = 0;
                for (int j = 0; j < i; j++)
                {
                    FP64 determinant = FPVector2.Cross(_orcaLines[i].direction, _orcaLines[j].direction);
                    FPOrcaLine line;

                    if (FP64.Abs(determinant) <= LP3_PARALLEL_EPSILON)
                    {
                        // Parallel lines — skip if same direction, midpoint if opposing
                        if (FPVector2.Dot(_orcaLines[i].direction, _orcaLines[j].direction) > FP64.Zero)
                            continue;

                        line.point = (_orcaLines[i].point + _orcaLines[j].point) * FP64.Half;
                    }
                    else
                    {
                        line.point = _orcaLines[i].point + _orcaLines[i].direction *
                            (FPVector2.Cross(_orcaLines[j].direction,
                                _orcaLines[i].point - _orcaLines[j].point) / determinant);
                    }

                    // Bisector: boundary where violation(j) <= violation(i)
                    line.direction = (_orcaLines[j].direction - _orcaLines[i].direction).normalized;
                    _projLines[projCount++] = line;
                }

                FPVector2 tempResult = result;
                FPVector2 optDir = new FPVector2(-_orcaLines[i].direction.y, _orcaLines[i].direction.x);
                if (LinearProgram2D(_projLines, projCount, maxSpeed, optDir, true,
                    LP3_PARALLEL_EPSILON, ref result) < projCount)
                {
                    // Numerical corner: keep the previous best result (RVO2 verbatim)
                    result = tempResult;
                }

                distance = FPVector2.Cross(_orcaLines[i].direction, _orcaLines[i].point - result);
            }
        }

    }
}
