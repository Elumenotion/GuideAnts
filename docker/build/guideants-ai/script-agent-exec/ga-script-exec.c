#define _GNU_SOURCE
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <sys/prctl.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: ga-script-exec <program> [args...]\n");
        return 64;
    }

    if (prctl(PR_SET_DUMPABLE, 0, 0, 0, 0) != 0) {
        fprintf(stderr, "failed to set PR_SET_DUMPABLE=0: %s\n", strerror(errno));
        return 70;
    }

    execvp(argv[1], &argv[1]);
    fprintf(stderr, "failed to exec '%s': %s\n", argv[1], strerror(errno));
    return 127;
}
