namespace SampleApi.Exceptions;

public class RegraNegocioException(string message) : Exception(message);

public class NaoEncontradoException(string message) : Exception(message);
