#pragma once

#include <algorithm>
#include <cstring>
#include <vector>

/**
 * Fixed delay per channel, sized once in prepare(). Feeds the bypass path of plugins
 * without a bypass parameter so dry stays time-aligned with the latency-delayed wet.
 */
class DelayLine
{
public:
    void prepare(int channels, int delaySamples)
    {
        _delay    = std::max(0, delaySamples);
        _channels = std::max(0, channels);
        _ring.assign(static_cast<size_t>(_channels) * static_cast<size_t>(_delay), 0.0f);
        _pos = 0;
    }

    void clear() noexcept
    {
        std::fill(_ring.begin(), _ring.end(), 0.0f);
        _pos = 0;
    }

    int delay() const noexcept { return _delay; }

    /** in and out may alias. Channels past what prepare() sized for are copied straight through. */
    void process(float* const* in, float* const* out, int channels, int frames) noexcept
    {
        for (int ch = 0; ch < channels; ++ch)
        {
            if (!out[ch]) continue;
            if (!in[ch]) { std::memset(out[ch], 0, sizeof(float) * static_cast<size_t>(frames)); continue; }

            if (_delay == 0 || ch >= _channels)
            {
                if (out[ch] != in[ch]) std::memmove(out[ch], in[ch], sizeof(float) * static_cast<size_t>(frames));
                continue;
            }

            float* ring = _ring.data() + static_cast<size_t>(ch) * static_cast<size_t>(_delay);
            int    pos  = _pos;

            for (int i = 0; i < frames; ++i)
            {
                const float x = in[ch][i];
                out[ch][i] = ring[pos];
                ring[pos]  = x;
                if (++pos == _delay) pos = 0;
            }
        }

        if (_delay > 0) _pos = static_cast<int>((static_cast<long long>(_pos) + frames) % _delay);
    }

private:
    std::vector<float> _ring;
    int _delay    = 0;
    int _channels = 0;
    int _pos      = 0;
};
