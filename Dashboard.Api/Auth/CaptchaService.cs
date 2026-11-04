using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dashboard.Api.Auth;

public class CaptchaService
{
    public const int BoardWidth = 168;
    public const int BoardHeight = 280;
    public const int PieceSize = 52;
    public const int TabRadius = 12;
    public const int PieceHeight = PieceSize + TabRadius;
    public const int PieceLeft = (BoardWidth - PieceSize) / 2;
    public const int SliderMax = BoardHeight - PieceHeight;
    public const int Tolerance = 8;

    private const int MaxMisses = 5;
    private readonly IMemoryCache _cache;

    public CaptchaService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public string Create()
    {
        var id = Guid.NewGuid().ToString("N");
        var targetY = RandomNumberGenerator.GetInt32(72, SliderMax + 1);
        using var board = PaintBoard();
        using var piece = new Image<Rgba32>(BoardWidth, PieceHeight);
        Cut(board, piece, targetY);
        _cache.Set(Key(id), new Challenge(Png(board), Png(piece), targetY), TimeSpan.FromMinutes(5));
        return id;
    }

    public byte[]? Board(string id) => Find(id)?.Board;

    public byte[]? Piece(string id) => Find(id)?.Piece;

    internal int? Target(string id) => Find(id)?.Target;

    public bool TooManyTries(string ip)
    {
        return _cache.TryGetValue(FailKey(ip), out int misses) && misses >= MaxMisses;
    }

    public void RegisterMiss(string ip)
    {
        var misses = _cache.TryGetValue(FailKey(ip), out int current) ? current + 1 : 1;
        _cache.Set(FailKey(ip), misses, TimeSpan.FromMinutes(2));
    }

    public void ClearMisses(string ip)
    {
        _cache.Remove(FailKey(ip));
    }

    public bool Verify(string? id, string? answer)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
        {
            return false;
        }

        if (!_cache.TryGetValue(Key(id), out Challenge? challenge) || challenge is null)
        {
            return false;
        }

        _cache.Remove(Key(id));
        return Math.Abs(offset - challenge.Target) <= Tolerance;
    }

    private Challenge? Find(string id)
    {
        return _cache.TryGetValue(Key(id), out Challenge? challenge) ? challenge : null;
    }

    private static Image<Rgba32> PaintBoard()
    {
        var rng = new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
        var image = new Image<Rgba32>(BoardWidth, BoardHeight);
        var blobs = new (int X, int Y, int Radius, Rgba32 Color)[10];
        for (var i = 0; i < blobs.Length; i++)
        {
            blobs[i] = (
                rng.Next(BoardWidth),
                rng.Next(BoardHeight),
                rng.Next(24, 70),
                new Rgba32((byte)rng.Next(30, 255), (byte)rng.Next(50, 255), (byte)rng.Next(80, 255)));
        }

        for (var y = 0; y < BoardHeight; y++)
        {
            for (var x = 0; x < BoardWidth; x++)
            {
                var t = (x + y) / (float)(BoardWidth + BoardHeight);
                var r = (int)(28 + 90 * t);
                var g = (int)(70 + 80 * (1 - t));
                var b = (int)(160 - 40 * t);
                if ((x / 6 + y / 8) % 3 == 0)
                {
                    r += 26;
                    b += 18;
                }

                foreach (var blob in blobs)
                {
                    var dx = x - blob.X;
                    var dy = y - blob.Y;
                    if (dx * dx + dy * dy > blob.Radius * blob.Radius)
                    {
                        continue;
                    }

                    r = (r + blob.Color.R) / 2;
                    g = (g + blob.Color.G) / 2;
                    b = (b + blob.Color.B) / 2;
                }

                var grain = rng.Next(-12, 13);
                image[x, y] = new Rgba32(Clamp(r + grain), Clamp(g + grain), Clamp(b + grain));
            }
        }

        for (var i = 0; i < 18; i++)
        {
            DrawLine(
                image,
                rng.Next(BoardWidth),
                rng.Next(BoardHeight),
                rng.Next(BoardWidth),
                rng.Next(BoardHeight),
                new Rgba32((byte)rng.Next(255), (byte)rng.Next(255), (byte)rng.Next(255)));
        }

        return image;
    }

    private static void Cut(Image<Rgba32> board, Image<Rgba32> piece, int targetY)
    {
        for (var ly = 0; ly < PieceHeight; ly++)
        {
            for (var lx = 0; lx < PieceSize; lx++)
            {
                if (!Inside(lx, ly))
                {
                    continue;
                }

                var color = board[PieceLeft + lx, targetY + ly];
                piece[PieceLeft + lx, ly] = color;
                board[PieceLeft + lx, targetY + ly] = new Rgba32(
                    (byte)(color.R * 0.38),
                    (byte)(color.G * 0.38),
                    (byte)(color.B * 0.38));
            }
        }

        for (var ly = 0; ly < PieceHeight; ly++)
        {
            for (var lx = 0; lx < PieceSize; lx++)
            {
                if (!Inside(lx, ly) || !IsEdge(lx, ly))
                {
                    continue;
                }

                board[PieceLeft + lx, targetY + ly] = new Rgba32(226, 240, 255);
                piece[PieceLeft + lx, ly] = new Rgba32(255, 255, 255);
            }
        }
    }

    private static bool Inside(int lx, int ly)
    {
        if (lx < 0 || lx >= PieceSize || ly < 0 || ly >= PieceHeight)
        {
            return false;
        }

        var inSquare = ly < PieceSize;
        var tabDx = lx - PieceSize / 2;
        var tabDy = ly - (PieceSize - 1);
        var inTab = tabDx * tabDx + tabDy * tabDy <= TabRadius * TabRadius;
        var notchDy = ly;
        var inNotch = tabDx * tabDx + notchDy * notchDy <= 9 * 9 && ly < 12;
        return (inSquare || inTab) && !inNotch;
    }

    private static bool IsEdge(int lx, int ly)
    {
        return !Inside(lx - 1, ly) || !Inside(lx + 1, ly) || !Inside(lx, ly - 1) || !Inside(lx, ly + 1);
    }

    private static void DrawLine(Image<Rgba32> image, int x1, int y1, int x2, int y2, Rgba32 color)
    {
        var steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        if (steps == 0)
        {
            return;
        }

        for (var i = 0; i <= steps; i++)
        {
            var x = x1 + (x2 - x1) * i / steps;
            var y = y1 + (y2 - y1) * i / steps;
            image[x, y] = color;
            if (x + 1 < BoardWidth)
            {
                image[x + 1, y] = color;
            }
        }
    }

    private static byte[] Png(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static string Key(string id) => "captcha:" + id;

    private static string FailKey(string ip) => "captcha-fail:" + ip;

    private sealed record Challenge(byte[] Board, byte[] Piece, int Target);
}
