// Own one rclone bridge and its mount. Clean up even if the managed app crashes.
#include <sys/event.h>
#include <sys/mount.h>
#include <sys/wait.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern char **environ;

int main(int argc, char **argv)
{
    if (argc < 5) return 64; // mountpoint, read-cache directory, rclone executable, arguments
    pid_t owner = getppid();
    int queue = kqueue();
    if (queue < 0) return 71;
    fcntl(queue, F_SETFD, FD_CLOEXEC);
    signal(SIGTERM, SIG_IGN);
    signal(SIGINT, SIG_IGN);
    struct kevent changes[3];
    EV_SET(&changes[0], owner, EVFILT_PROC, EV_ADD, NOTE_EXIT, 0, NULL);
    EV_SET(&changes[1], SIGTERM, EVFILT_SIGNAL, EV_ADD, 0, 0, NULL);
    EV_SET(&changes[2], SIGINT, EVFILT_SIGNAL, EV_ADD, 0, 0, NULL);
    if (owner <= 1 || kevent(queue, changes, 3, NULL, 0, NULL) < 0) return 71;

    posix_spawnattr_t attr;
    posix_spawnattr_init(&attr);
    sigset_t defaults;
    sigemptyset(&defaults);
    sigaddset(&defaults, SIGTERM);
    sigaddset(&defaults, SIGINT);
    posix_spawnattr_setsigdefault(&attr, &defaults);
    posix_spawnattr_setflags(&attr, POSIX_SPAWN_SETSIGDEF);
    pid_t child;
    int error = posix_spawn(&child, argv[3], NULL, &attr, &argv[3], environ);
    posix_spawnattr_destroy(&attr);
    if (error) { fprintf(stderr, "storage bridge spawn: %s\n", strerror(error)); return 71; }
    struct kevent change, event;
    EV_SET(&change, child, EVFILT_PROC, EV_ADD, NOTE_EXIT, 0, NULL);
    if (kevent(queue, &change, 1, NULL, 0, NULL) == 0) {
        while (kevent(queue, NULL, 0, &event, 1, NULL) < 0 && errno == EINTR) { }
    }

    // The bridge uses synchronous writes, with no deferred disk write-back cache.
    // Stop new filesystem access before taking its server away. A crashed owner
    // must never leave a Finder volume pointing to an orphaned local service.
    struct statfs fs;
    char canonical[PATH_MAX];
    if (realpath(argv[1], canonical) && statfs(canonical, &fs) == 0 &&
        strcmp(fs.f_fstypename, "webdav") == 0 && strcmp(fs.f_mntonname, canonical) == 0) {
        if (unmount(canonical, 0) < 0) unmount(canonical, MNT_FORCE);
    }
    kill(child, SIGTERM);
    // Read cache files are opened DeleteOnClose (unlinked on Unix). After a
    // parent crash the empty private directory can also be removed safely.
    rmdir(argv[2]);
    int status = 0;
    // rclone normally exits immediately. Bound shutdown if its SFTP peer is gone.
    for (int i = 0; i < 50; i++) {
        pid_t result = waitpid(child, &status, WNOHANG);
        if (result == child || (result < 0 && errno == ECHILD)) { close(queue); return 0; }
        usleep(100000);
    }
    kill(child, SIGKILL);
    waitpid(child, &status, 0);
    close(queue);
    return 0;
}
