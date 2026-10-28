"""Public numeric-only OS diagnostics; never serialize an original exception.

No storage, retries, recovery, or reservation changes happen in this helper.
"""
from contextlib import contextmanager
from enum import Enum
import json


class Stage(Enum):
    EXCHANGE = 'exchange'
    START = 'stage_start'
    RESERVATION = 'reservation'
    DIRECTORY = 'stage_directory'
    REQUEST = 'request'
    RESPONSE = 'response'
    RECEIPT = 'receipt'
    OUTCOME = 'outcome'
    FINISH = 'stage_finish'
    ARTIFACT = 'artifact'
    TRANSPORT = 'transport'


class Operation(Enum):
    DISPATCH = 'dispatch'
    JOURNAL_APPEND = 'journal_append'
    MKDIR = 'mkdir'
    OPEN = 'open_create_new'
    WRITE = 'write'
    FLUSH = 'flush'
    FSYNC = 'fsync'
    CLOSE = 'close'
    SPAWN = 'spawn'


class SafeOSError(OSError):
    """A terminal OS failure with only reviewed identifiers and integer codes."""

    def __init__(self, stage, operation, error):
        if type(stage) is not Stage or type(operation) is not Operation:
            raise TypeError('reviewed diagnostic identifiers required')
        self.diagnostic = {'kind': 'os_error', 'stage': stage.value,
                           'operation': operation.value}
        for field in ('errno', 'winerror'):
            try:
                value = getattr(error, field, None)
            except BaseException:
                # Diagnostic getters are untrusted subclass code. A secondary
                # failure must never mask the original terminal OS failure.
                value = None
            # bool/int subclasses can supply surprising serialization behavior.
            # Bound numeric output to signed/unsigned 32-bit OS error codes.
            self.diagnostic[field] = (value if type(value) is int
                                      and -(2 ** 31) <= value < 2 ** 32 else None)
        try:
            message = json.dumps(self.diagnostic, sort_keys=True)
        except BaseException:
            # Keep the structured fields available even if the serializer fails.
            # Do not stringify either the OS error or the serializer failure.
            message = 'terminal OS error; safe diagnostic available'
        super().__init__(message)


@contextmanager
def safe_os(stage, operation):
    """Re-raise terminal OS failures without exposing text, paths, or context.

    Inner boundaries retain their more precise identifiers. Callers must stop;
    this is diagnostic enrichment, never evidence that an operation did not run.
    """
    if type(stage) is not Stage or type(operation) is not Operation:
        raise TypeError('reviewed diagnostic identifiers required')
    try:
        yield
    except SafeOSError:
        raise
    except OSError as error:
        raise SafeOSError(stage, operation, error) from None
