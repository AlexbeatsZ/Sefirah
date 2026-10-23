// Sefirah's local WebDAV transport. Standard-library reverse proxying preserves
// DAV lock semantics; complete GET bodies are checked before webdavfs sees them.
package main

import (
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/signal"
	"strings"
	"syscall"
)

func run() error {
	if len(os.Args) != 3 {
		return fmt.Errorf("expected backend URL and private read-cache directory")
	}
	backend, err := url.Parse(os.Args[1])
	if err != nil || backend.Scheme != "http" || backend.Hostname() != "127.0.0.1" {
		return fmt.Errorf("invalid loopback backend")
	}
	cache := os.Args[2]
	if err := os.MkdirAll(cache, 0700); err != nil {
		return err
	}
	defer os.Remove(cache) // Only empty local directories; never recursive.
	queue, err := syscall.Kqueue()
	if err != nil {
		return err
	}
	defer syscall.Close(queue)
	syscall.CloseOnExec(queue)
	owner := os.Getppid()
	if owner <= 1 {
		return fmt.Errorf("storage owner exited")
	}
	_, err = syscall.Kevent(queue, []syscall.Kevent_t{{Ident: uint64(owner), Filter: syscall.EVFILT_PROC, Flags: syscall.EV_ADD, Fflags: syscall.NOTE_EXIT}}, nil, nil)
	if err != nil {
		return err
	}
	done := make(chan struct{}, 1)
	go func() {
		var event [1]syscall.Kevent_t
		for {
			n, err := syscall.Kevent(queue, nil, event[:], nil)
			if err == syscall.EINTR {
				continue
			}
			if err != nil || n > 0 {
				done <- struct{}{}
				return
			}
		}
	}()
	interrupt := make(chan os.Signal, 1)
	signal.Notify(interrupt, syscall.SIGTERM, syscall.SIGINT)
	defer signal.Stop(interrupt)
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer listener.Close()
	front := "http://" + listener.Addr().String()
	basePath := backend.EscapedPath()
	backendAuthority := backend.Scheme + "://" + backend.Host
	target := &url.URL{Scheme: backend.Scheme, Host: backend.Host}
	proxy := httputil.NewSingleHostReverseProxy(target)
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.Proxy = nil
	transport.DisableCompression = true
	proxy.Transport = transport
	defer transport.CloseIdleConnections()
	direct := proxy.Director
	proxy.Director = func(r *http.Request) {
		direct(r)
		r.Host = backend.Host
		// Keep the backend's original byte count. Automatic gzip can turn a
		// failed file read into a valid empty gzip stream with unknown length.
		r.Header.Set("Accept-Encoding", "identity")
		for _, key := range []string{"Destination", "If"} {
			if value := r.Header.Get(key); value != "" {
				r.Header.Set(key, strings.ReplaceAll(value, front, backendAuthority))
			}
		}
	}
	proxy.ModifyResponse = func(response *http.Response) error {
		if location := response.Header.Get("Location"); location != "" {
			response.Header.Set("Location", strings.ReplaceAll(location, backendAuthority, front))
		}
		if response.Request.Method != "GET" || response.StatusCode < 200 || response.StatusCode >= 300 {
			return nil
		}
		file, err := os.CreateTemp(cache, "read-")
		if err != nil {
			response.Body.Close()
			return err
		}
		os.Remove(file.Name()) // Automatic cleanup even after SIGKILL.
		n, err := io.Copy(file, response.Body)
		response.Body.Close()
		if err == nil && response.ContentLength >= 0 && n != response.ContentLength {
			err = io.ErrUnexpectedEOF
		}
		if err != nil {
			file.Close()
			return err
		}
		if _, err = file.Seek(0, io.SeekStart); err != nil {
			file.Close()
			return err
		}
		response.Body = file
		response.ContentLength = n
		response.Header.Set("Content-Length", fmt.Sprint(n))
		response.TransferEncoding = nil
		return nil
	}
	// Errors can contain remote filenames and capability URLs. Do not log them.
	proxy.ErrorHandler = func(w http.ResponseWriter, r *http.Request, err error) {
		http.Error(w, "Phone storage unavailable", http.StatusBadGateway)
	}
	server := &http.Server{Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasPrefix(r.URL.EscapedPath(), basePath) {
			http.NotFound(w, r)
			return
		}
		proxy.ServeHTTP(w, r)
	}), MaxHeaderBytes: 64 * 1024}
	finished := make(chan error, 1)
	go func() { finished <- server.Serve(listener) }()
	fmt.Println(front + basePath)
	select {
	case <-done:
	case <-interrupt:
	case err = <-finished:
	}
	server.Close()
	return err
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "Storage proxy stopped.")
		os.Exit(1)
	}
}
