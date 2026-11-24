namespace BpCore;

/// <summary>Причина, по которой замер не вошёл в среднее серии.</summary>
public enum Exclusion
{
    None,
    /// <summary>Физиологически неправдоподобный замер: скорее сбой аппарата.</summary>
    Glitch,
    /// <summary>Сильно отличается от медианы серии.</summary>
    Outlier,
}

/// <summary>Один замер давления.</summary>
public sealed class Measurement
{
    public DateTime Time { get; init; }
    public int Sys { get; init; }
    public int Dia { get; init; }
    public int Map { get; init; }
    public int Pulse { get; init; }
    public int PulsePressure => Sys - Dia;

    /// <summary>Откуда замер: аппарат, имя файла .awp/.pdf, архив.</summary>
    public string Source { get; set; } = "";

    // Заполняются анализом
    public int SeriesNo { get; set; }
    public Exclusion Excluded { get; set; }

    public string ExcludedText => Excluded switch
    {
        Exclusion.Glitch => "сбой",
        Exclusion.Outlier => "выброс",
        _ => "",
    };

    /// <summary>Ключ для отсева повторов при объединении источников.</summary>
    public (DateTime, int, int, int) Key => (Time, Sys, Dia, Pulse);

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm} {Sys}/{Dia} пульс {Pulse}";
}
