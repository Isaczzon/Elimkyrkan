document.addEventListener('DOMContentLoaded', () => {
    // Kom ihåg besökarens språk till nästa besök
    try {
        const culture = document.documentElement.getAttribute('data-culture');
        if (culture) localStorage.setItem('lang', culture);
    } catch (e) { /* ignore */ }

    // Spara språkvalet direkt vid klick i språkmenyn — annars hinner
    // startsidans omdirigering skicka tillbaka besökaren till det gamla språket
    document.querySelectorAll('.lang-menu a').forEach((a) =>
        a.addEventListener('click', () => {
            try { localStorage.setItem('lang', a.getAttribute('hreflang')); } catch (e) { /* ignore */ }
        })
    );

    const nav = document.getElementById('navbar');
    if (nav) {
        window.addEventListener('scroll', () => {
            nav.classList.toggle('scrolled', window.scrollY > 10);
        });
    }

    const hamburger = document.querySelector('.hamburger');
    const navLinks = document.getElementById('navLinks');
    if (hamburger && navLinks) {
        hamburger.addEventListener('click', () => navLinks.classList.toggle('open'));
        navLinks.querySelectorAll('a').forEach((a) =>
            a.addEventListener('click', () => navLinks.classList.remove('open'))
        );
    }

    const observer = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) entry.target.classList.add('visible');
        });
    }, { threshold: 0.1 });
    document.querySelectorAll('.fade-in').forEach(el => observer.observe(el));

    // YouTube-fasad: ladda iframen först när besökaren klickar (snabbare, inga kakor i förväg)
    document.querySelectorAll('.video-fasad').forEach((knapp) => {
        knapp.addEventListener('click', () => {
            const iframe = document.createElement('iframe');
            iframe.src = 'https://www.youtube-nocookie.com/embed/' + knapp.dataset.videoId + '?autoplay=1';
            iframe.title = knapp.dataset.videoTitel || 'YouTube video';
            iframe.allow = 'accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture';
            iframe.referrerPolicy = 'strict-origin-when-cross-origin';
            iframe.allowFullscreen = true;
            knapp.replaceWith(iframe);
        });
    });

    const themeToggle = document.getElementById('themeToggle');
    if (themeToggle) {
        const updateIcon = () => {
            const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
            themeToggle.textContent = isDark ? '☀️' : '🌙';
        };
        updateIcon();
        themeToggle.addEventListener('click', () => {
            const current = document.documentElement.getAttribute('data-theme') || 'light';
            const next = current === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-theme', next);
            try { localStorage.setItem('theme', next); } catch (e) { /* ignore */ }
            updateIcon();
        });
    }
});
