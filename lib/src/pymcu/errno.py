# MicroPython-compatible errno module.
#
# The codes and names are exactly the set upstream's py/moderrno.c exports (the
# X-list every port shares), with the values of py/mperrno.h -- the same table
# BuiltinErrnoNames carries for the `[Errno n] NAME` text print(OSError(n))
# emits, so a code that prints a name here prints the same name there.
#
# A handler reads the code of a caught OSError as exc.errno (also exc.args[0])
# when every raise it can catch carried an integer argument.

EPERM = 1
ENOENT = 2
EIO = 5
EBADF = 9
EAGAIN = 11
ENOMEM = 12
EACCES = 13
EEXIST = 17
ENODEV = 19
EISDIR = 21
EINVAL = 22
EOPNOTSUPP = 95
EADDRINUSE = 98
ECONNABORTED = 103
ECONNRESET = 104
ENOBUFS = 105
ENOTCONN = 107
ETIMEDOUT = 110
ECONNREFUSED = 111
EHOSTUNREACH = 113
EALREADY = 114
EINPROGRESS = 115

errorcode = {
    1: "EPERM",
    2: "ENOENT",
    5: "EIO",
    9: "EBADF",
    11: "EAGAIN",
    12: "ENOMEM",
    13: "EACCES",
    17: "EEXIST",
    19: "ENODEV",
    21: "EISDIR",
    22: "EINVAL",
    95: "EOPNOTSUPP",
    98: "EADDRINUSE",
    103: "ECONNABORTED",
    104: "ECONNRESET",
    105: "ENOBUFS",
    107: "ENOTCONN",
    110: "ETIMEDOUT",
    111: "ECONNREFUSED",
    113: "EHOSTUNREACH",
    114: "EALREADY",
    115: "EINPROGRESS",
}
