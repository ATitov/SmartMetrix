namespace SmartMetrix.LocalPositioningService;

public interface IPositioningAdapter
{
    PositionSourceType SourceType { get; }
    IAsyncEnumerable<PositioningSample> ReadAsync(CancellationToken cancellationToken);
}

public interface ITotalStationAdapter : IPositioningAdapter;
public interface IImuAdapter : IPositioningAdapter;
public interface IEncoderAdapter : IPositioningAdapter;
public interface IGnssAdapter : IPositioningAdapter;

// Hardware integrations implement the typed ports above. The HTTP ingestion endpoint uses
// the same PositioningSample envelope, so timestamps and provenance are preserved end-to-end.
