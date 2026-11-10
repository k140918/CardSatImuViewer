using System.IO;
using System.Text.Json;

namespace CardSatAttitudeGame;

public sealed record RankingEntry(string Name, Difficulty Difficulty, int Docked, double AverageSeconds, DateTime PlayedAt);

/// <summary>
/// ランキングを JSON で保存する（%LOCALAPPDATA%\CardSatAttitudeGame\ranking.json）。
/// 並び順：成功回数が多い順 → 平均時間が短い順 → 先に出した記録が上。
/// </summary>
public sealed class RankingStore
{
    public const int TopCount = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<RankingEntry> _entries = new();

    public RankingStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CardSatAttitudeGame");
        _path = Path.Combine(dir, "ranking.json");
        Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<RankingEntry> Top(Difficulty difficulty, int count = TopCount) =>
        Sorted(difficulty).Take(count).ToList();

    /// <summary>記録を追加して保存し、その難易度での順位（1 始まり）を返す</summary>
    public int Add(RankingEntry entry)
    {
        _entries.Add(entry);
        Save();
        int index = Sorted(entry.Difficulty).ToList().IndexOf(entry);
        return index + 1;
    }

    /// <summary>その難易度の記録をすべて消す</summary>
    public void Clear(Difficulty difficulty)
    {
        _entries.RemoveAll(e => e.Difficulty == difficulty);
        Save();
    }

    private IEnumerable<RankingEntry> Sorted(Difficulty difficulty) =>
        _entries
            .Where(e => e.Difficulty == difficulty)
            .OrderByDescending(e => e.Docked)
            .ThenBy(e => e.Docked > 0 ? e.AverageSeconds : double.MaxValue)
            .ThenBy(e => e.PlayedAt);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var list = JsonSerializer.Deserialize<List<RankingEntry>>(File.ReadAllText(_path), JsonOptions);
            if (list is not null)
                _entries.AddRange(list);
        }
        catch
        {
            // 壊れていたら空から始める（ファイルは上書きされる）
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch
        {
            // 保存できなくてもゲームは続ける
        }
    }
}
