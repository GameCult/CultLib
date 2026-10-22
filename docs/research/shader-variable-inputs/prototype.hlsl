// The prototype, appended to CultMath.hlsl by probe.sh: the whole weighted_lattice_cells with bodies as a fixed-size array and a count.
static const int CULTMATH_MAX_SITES = 16;

float4 cultmath_power_border(float3 p, float4 sites[CULTMATH_MAX_SITES], int count)
{
    CultSiteCells cells = cultmath_site_cells_empty();
    for (int i = 0; i < count; i++)
        cells = cultmath_site_cells_insert(cells, cultmath_power_score(p, sites[i]), i);
    float4 border = float4(0.0, 0.0, 0.0, 1.0e30);
    int owner = (int)cells.nearestSite;
    if (owner < 0)
        return border;
    for (int k = 0; k < count; k++)
        if (k != owner)
            border = cultmath_power_border_step(p, sites[owner], sites[k], border);
    return border;
}

float cultmath_lattice_cell_distance(float2 q, float2 cell, float time, float drift, float4 bodies[CULTMATH_MAX_SITES], int count,
    float cellSize, float boost, float falloff, float2 normal, float stretch)
{
    float3 site = cultmath_lattice_site(cell, time, drift);
    float border = cultmath_power_border(float3((cell + 0.5) * cellSize, 0.0), bodies, count).w;
    return cultmath_lattice_distance(q, site.xy, cultmath_lattice_weight(site.z, border, boost, falloff), normal, stretch);
}

float4 cultmath_weighted_lattice_cells(float2 p, float time, float4 bodies[CULTMATH_MAX_SITES], int count, float cellSize, float drift,
    float boost, float falloff, float smoothing, float2 stretchNormal, float stretch)
{
    float scale = max(abs(stretchNormal.x), abs(stretchNormal.y));
    float2 normal = float2(1.0, 0.0);
    if (scale > 0.0)
    {
        normal = stretchNormal / scale;
        normal = normal / length(normal);
    }
    float2 q = p / cellSize;
    float2 origin = floor(q);
    int cx0 = (int)origin.x;
    int cz0 = (int)origin.y;
    float f1 = 1.0e30;
    float f2 = 1.0e30;
    int reach = 0;
    for (int k = 0; k <= 37; k++)
    {
        if (k > 1 && 0.5 * (k - 1) >= max(f2, f1 + 2.0 * smoothing))
            break;
        for (int dz = -k; dz <= k; dz++)
        {
            int step = dz == -k || dz == k || k == 0 ? 1 : 2 * k;
            for (int dx = -k; dx <= k; dx += step)
            {
                float d = cultmath_lattice_cell_distance(q, float2(cx0 + dx, cz0 + dz), time, drift, bodies, count, cellSize, boost, falloff, normal, stretch);
                if (d < f1) { f2 = f1; f1 = d; }
                else if (d < f2) f2 = d;
            }
        }
        reach = k;
    }
    float m = f1 + smoothing;
    float best = 1.0e30;
    float second = 1.0e30;
    int winDx = 0;
    int winDz = 0;
    for (int dz = -reach; dz <= reach; dz++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            float d = cultmath_lattice_cell_distance(q, float2(cx0 + dx, cz0 + dz), time, drift, bodies, count, cellSize, boost, falloff, normal, stretch);
            if (d < best) { second = best; best = d; winDx = dx; winDz = dz; }
            else if (d < second) second = d;
            m = cultmath_lattice_smooth_step(m, d, f1, smoothing);
        }
    int4 id = cultmath_pcg4d(int4(cx0 + winDx, cz0 + winDz, 0, 0));
    return float4(best, second, m, (float)((uint)id.w >> 8) * (1.0 / 16777216.0));
}
