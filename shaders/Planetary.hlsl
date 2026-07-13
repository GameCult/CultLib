#ifndef CULTMATH_PLANETARY_HLSL
#define CULTMATH_PLANETARY_HLSL

static const float CULTMATH_PLANETARY_QUARTER_PI = 0.78539816339744830962;

float3 cultmath_planetary_face_direction(int face, float2 coordinate)
{
    float2 tangent = tan(coordinate * CULTMATH_PLANETARY_QUARTER_PI);
    float3 cube = face == 0 ? float3(1.0, tangent.y, -tangent.x)
        : face == 1 ? float3(-1.0, tangent.y, tangent.x)
        : face == 2 ? float3(tangent.x, 1.0, -tangent.y)
        : face == 3 ? float3(tangent.x, -1.0, tangent.y)
        : face == 4 ? float3(tangent.x, tangent.y, 1.0)
        : float3(-tangent.x, tangent.y, -1.0);
    return cultmath_normalize(cube);
}

float2 cultmath_planetary_face_coordinate(float3 direction, out int face)
{
    float3 d = cultmath_normalize(direction);
    float3 a = abs(d);
    float2 cube_uv;
    if (a.x >= a.y && a.x >= a.z)
    {
        if (d.x >= 0.0) { face = 0; cube_uv = float2(-d.z, d.y) / a.x; }
        else { face = 1; cube_uv = float2(d.z, d.y) / a.x; }
    }
    else if (a.y >= a.z)
    {
        if (d.y >= 0.0) { face = 2; cube_uv = float2(d.x, -d.z) / a.y; }
        else { face = 3; cube_uv = float2(d.x, d.z) / a.y; }
    }
    else
    {
        if (d.z >= 0.0) { face = 4; cube_uv = d.xy / a.z; }
        else { face = 5; cube_uv = float2(-d.x, d.y) / a.z; }
    }
    return atan(cube_uv) / CULTMATH_PLANETARY_QUARTER_PI;
}

bool cultmath_planetary_page_local(float3 direction, float4 tile_address, float active, out float2 local)
{
    int face;
    float2 uv = cultmath_planetary_face_coordinate(direction, face);
    local = 0.0;
    if (active < 0.5 || abs((float)face - tile_address.x) > 0.25) return false;
    float axis_tiles = exp2(tile_address.y);
    float2 scaled = (uv * 0.5 + 0.5) * axis_tiles;
    float2 canonical = floor(min(scaled, axis_tiles - 1.0e-5));
    if (any(abs(canonical - tile_address.zw) > 0.25)) return false;
    local = scaled - tile_address.zw;
    return all(local >= 0.0) && all(local <= 1.0);
}

float3 cultmath_planetary_surface_normal(float3 direction, float3 world_distance_tangent_gradient)
{
    direction = cultmath_normalize(direction);
    float3 tangent_gradient = world_distance_tangent_gradient - direction * dot(world_distance_tangent_gradient, direction);
    return cultmath_normalize(direction - tangent_gradient);
}

float2 cultmath_planetary_equirectangular_forward(float3 direction)
{
    direction = cultmath_normalize(direction);
    return float2(atan2(direction.y, direction.x) / CULTMATH_PI, asin(clamp(direction.z, -1.0, 1.0)) / CULTMATH_HALF_PI);
}

float3 cultmath_planetary_equirectangular_inverse(float2 coordinate)
{
    float longitude = coordinate.x * CULTMATH_PI;
    float latitude = coordinate.y * CULTMATH_HALF_PI;
    float cos_latitude = cos(latitude);
    return cultmath_normalize(float3(cos_latitude * cos(longitude), cos_latitude * sin(longitude), sin(latitude)));
}

static const float CULTMATH_EQUAL_EARTH_A1 = 1.340264;
static const float CULTMATH_EQUAL_EARTH_A2 = -0.081106;
static const float CULTMATH_EQUAL_EARTH_A3 = 0.000893;
static const float CULTMATH_EQUAL_EARTH_A4 = 0.003796;
static const float CULTMATH_EQUAL_EARTH_M = 0.8660254037844386;
static const float CULTMATH_EQUAL_EARTH_X_MAX = 2.706629;
static const float CULTMATH_EQUAL_EARTH_Y_MAX = 1.3173628;

float cultmath_equal_earth_y(float theta)
{
    float theta2 = theta * theta;
    float theta6 = theta2 * theta2 * theta2;
    return theta * (CULTMATH_EQUAL_EARTH_A1 + CULTMATH_EQUAL_EARTH_A2 * theta2 + theta6 * (CULTMATH_EQUAL_EARTH_A3 + CULTMATH_EQUAL_EARTH_A4 * theta2));
}

float2 cultmath_planetary_equal_earth_forward(float3 direction)
{
    direction = cultmath_normalize(direction);
    float longitude = atan2(direction.y, direction.x);
    float latitude = asin(clamp(direction.z, -1.0, 1.0));
    float theta = asin(CULTMATH_EQUAL_EARTH_M * sin(latitude));
    float theta2 = theta * theta;
    float theta6 = theta2 * theta2 * theta2;
    float denominator = CULTMATH_EQUAL_EARTH_M * (CULTMATH_EQUAL_EARTH_A1 + 3.0 * CULTMATH_EQUAL_EARTH_A2 * theta2 + theta6 * (7.0 * CULTMATH_EQUAL_EARTH_A3 + 9.0 * CULTMATH_EQUAL_EARTH_A4 * theta2));
    return float2(longitude * cos(theta) / denominator / CULTMATH_EQUAL_EARTH_X_MAX, cultmath_equal_earth_y(theta) / CULTMATH_EQUAL_EARTH_Y_MAX);
}

float3 cultmath_planetary_equal_earth_inverse(float2 coordinate)
{
    float target_y = coordinate.y * CULTMATH_EQUAL_EARTH_Y_MAX;
    float theta = target_y / CULTMATH_EQUAL_EARTH_A1;
    [unroll] for (int iteration = 0; iteration < 8; iteration++)
    {
        float theta2 = theta * theta;
        float theta6 = theta2 * theta2 * theta2;
        float derivative = CULTMATH_EQUAL_EARTH_A1 + 3.0 * CULTMATH_EQUAL_EARTH_A2 * theta2 + theta6 * (7.0 * CULTMATH_EQUAL_EARTH_A3 + 9.0 * CULTMATH_EQUAL_EARTH_A4 * theta2);
        theta -= (cultmath_equal_earth_y(theta) - target_y) / derivative;
    }
    float latitude = asin(clamp(sin(theta) / CULTMATH_EQUAL_EARTH_M, -1.0, 1.0));
    float theta2 = theta * theta;
    float theta6 = theta2 * theta2 * theta2;
    float denominator = CULTMATH_EQUAL_EARTH_M * (CULTMATH_EQUAL_EARTH_A1 + 3.0 * CULTMATH_EQUAL_EARTH_A2 * theta2 + theta6 * (7.0 * CULTMATH_EQUAL_EARTH_A3 + 9.0 * CULTMATH_EQUAL_EARTH_A4 * theta2));
    float longitude = coordinate.x * CULTMATH_EQUAL_EARTH_X_MAX * denominator / cos(theta);
    float cos_latitude = cos(latitude);
    return cultmath_normalize(float3(cos_latitude * cos(longitude), cos_latitude * sin(longitude), sin(latitude)));
}

struct CultMathPlanetaryFieldDefinition
{
    float radius;
    int seed;
    CultMathAdvancedErosionParameters erosion;
};

struct CultMathPlanetaryBaseFieldSample
{
    float radial_displacement;
    float3 radial_gradient;
    float field_value;
    float3 field_gradient;
    float fade_target;
};

struct CultMathPlanetarySurfaceSample
{
    float radial_displacement;
    float3 tangent_gradient;
    float3 surface_normal;
    float slope;
    float ridge;
    float gully;
    float finest_resolved_wavelength;
    float unresolved_height_bound;
};

struct CultMathPlanetaryPageSample
{
    float4 height_gradient;
    float2 masks;
};

CultMathPlanetaryPageSample cultmath_planetary_page_lerp(
    CultMathPlanetaryPageSample a,
    CultMathPlanetaryPageSample b,
    CultMathPlanetaryPageSample c,
    CultMathPlanetaryPageSample d,
    float2 fraction)
{
    CultMathPlanetaryPageSample result;
    result.height_gradient = lerp(lerp(a.height_gradient, b.height_gradient, fraction.x), lerp(c.height_gradient, d.height_gradient, fraction.x), fraction.y);
    result.masks = lerp(lerp(a.masks, b.masks, fraction.x), lerp(c.masks, d.masks, fraction.x), fraction.y);
    return result;
}

CultMathPlanetaryPageSample cultmath_planetary_residual_sample(
    CultMathPlanetarySurfaceSample child,
    CultMathPlanetarySurfaceSample parent,
    bool has_parent)
{
    CultMathPlanetaryPageSample result;
    result.height_gradient = float4(child.radial_displacement, child.tangent_gradient);
    if (has_parent) result.height_gradient -= float4(parent.radial_displacement, parent.tangent_gradient);
    result.masks = float2(child.ridge, child.gully);
    return result;
}

float4 cultmath_planetary_summary_empty()
{
    return float4(3.402823466e+38, -3.402823466e+38, 0.0, 0.0);
}

float4 cultmath_planetary_summary_accumulate(float4 summary, float4 height_gradient, float unresolved_height)
{
    summary.x = min(summary.x, height_gradient.x);
    summary.y = max(summary.y, height_gradient.x);
    summary.z = max(summary.z, length(height_gradient.yzw));
    summary.w = max(summary.w, max(unresolved_height, 0.0));
    return summary;
}

float cultmath_planetary_pow4(float value) { float square = value * value; return square * square; }

CultMathPlanetarySurfaceSample cultmath_planetary_field_sample(
    CultMathPlanetaryFieldDefinition definition,
    float3 unit_direction,
    CultMathPlanetaryBaseFieldSample base_sample,
    float footprint_meters)
{
    float3 direction = cultmath_normalize(unit_direction);
    float3 radial_gradient = base_sample.radial_gradient - direction * dot(base_sample.radial_gradient, direction);
    float3 field_gradient = base_sample.field_gradient - direction * dot(base_sample.field_gradient, direction);
    CultMathAdvancedErosionParameters p = definition.erosion;
    CultMathErosionBandSelection band = cultmath_select_erosion_bands(
        p.scale * p.cell_scale, footprint_meters, p.octaves, p.lacunarity,
        p.strength * p.scale, p.gain, 2.0);
    float3 powers = float3(
        cultmath_planetary_pow4(abs(direction.x)),
        cultmath_planetary_pow4(abs(direction.y)),
        cultmath_planetary_pow4(abs(direction.z)));
    float power_sum = max(powers.x + powers.y + powers.z, 1.0e-6);
    float3 weights = powers / power_sum;
    float3 world = direction * definition.radius;
    float seed = (float)definition.seed;
    CultMathAdvancedErosionResult xy = cultmath_advanced_erosion_filter_banded(
        world.xy + float2(713.0 + seed * 0.754877666, -291.0 + seed * 0.569840296),
        float3(base_sample.field_value, field_gradient.x, field_gradient.y), base_sample.fade_target, p, band);
    CultMathAdvancedErosionResult yz = cultmath_advanced_erosion_filter_banded(
        world.yz + float2(-431.0 + seed * 0.438289027, 887.0 + seed * 0.328438163),
        float3(base_sample.field_value, field_gradient.y, field_gradient.z), base_sample.fade_target, p, band);
    CultMathAdvancedErosionResult zx = cultmath_advanced_erosion_filter_banded(
        world.zx + float2(197.0 + seed * 0.219783071, 557.0 + seed * 0.145898034),
        float3(base_sample.field_value, field_gradient.z, field_gradient.x), base_sample.fade_target, p, band);
    float erosion_height = xy.delta.x * weights.z + yz.delta.x * weights.x + zx.delta.x * weights.y;
    float3 erosion_gradient = float3(
        xy.delta.y * weights.z + zx.delta.z * weights.y,
        xy.delta.z * weights.z + yz.delta.y * weights.x,
        yz.delta.z * weights.x + zx.delta.y * weights.y);
    float3 power_gradient_x = float3(4.0 * direction.x * direction.x * direction.x / definition.radius, 0.0, 0.0);
    float3 power_gradient_y = float3(0.0, 4.0 * direction.y * direction.y * direction.y / definition.radius, 0.0);
    float3 power_gradient_z = float3(0.0, 0.0, 4.0 * direction.z * direction.z * direction.z / definition.radius);
    power_gradient_x -= direction * dot(power_gradient_x, direction);
    power_gradient_y -= direction * dot(power_gradient_y, direction);
    power_gradient_z -= direction * dot(power_gradient_z, direction);
    float3 power_sum_gradient = power_gradient_x + power_gradient_y + power_gradient_z;
    float inverse_power_sum_squared = 1.0 / (power_sum * power_sum);
    float3 weight_gradient_x = (power_gradient_x * power_sum - power_sum_gradient * powers.x) * inverse_power_sum_squared;
    float3 weight_gradient_y = (power_gradient_y * power_sum - power_sum_gradient * powers.y) * inverse_power_sum_squared;
    float3 weight_gradient_z = (power_gradient_z * power_sum - power_sum_gradient * powers.z) * inverse_power_sum_squared;
    erosion_gradient += yz.delta.x * weight_gradient_x + zx.delta.x * weight_gradient_y + xy.delta.x * weight_gradient_z;
    float3 gradient = radial_gradient + erosion_gradient;
    gradient -= direction * dot(gradient, direction);
    float ridge_evidence = xy.ridge_map * weights.z + yz.ridge_map * weights.x + zx.ridge_map * weights.y;
    CultMathPlanetarySurfaceSample result;
    result.radial_displacement = base_sample.radial_displacement + erosion_height;
    result.tangent_gradient = gradient;
    result.surface_normal = cultmath_planetary_surface_normal(direction, gradient);
    result.slope = length(gradient);
    result.ridge = saturate(ridge_evidence * 0.5 + 0.5);
    result.gully = saturate(0.5 - ridge_evidence * 0.5);
    result.finest_resolved_wavelength = band.finest_included_wavelength;
    result.unresolved_height_bound = band.unresolved_height_bound;
    return result;
}

#endif
