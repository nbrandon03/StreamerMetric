
const API_ORIGIN = window.location.port === "5168"
    ? window.location.origin
    : window.location.protocol === "file:"
        ? "http://localhost:5168"
        : `${window.location.protocol}//${window.location.hostname}:5168`;
const API_ROOT_URL = `${API_ORIGIN}/api/twitch`;
const API_BASE_URL = `${API_ROOT_URL}/chat`;
let broadcasterId = null;
let selectedStreamId = null;

async function fetchAnalytics(endpoint) {
    const streamQuery = selectedStreamId
        ? `?streamId=${encodeURIComponent(selectedStreamId)}`
        : "";

    const response = await fetch(
        `${API_BASE_URL}/analytics/${endpoint}/${broadcasterId}${streamQuery}`
    );

    if (!response.ok) {
        throw new Error(`API request failed: ${response.status}`);
    }

    return response.json();
}

async function fetchLeaderboard() {
    const streamQuery = selectedStreamId
        ? `?streamId=${encodeURIComponent(selectedStreamId)}`
        : "";

    const response = await fetch(
        `${API_BASE_URL}/leaderboard/${broadcasterId}${streamQuery}`
    );

    if (!response.ok) {
        throw new Error(`Leaderboard request failed: ${response.status}`);
    }

    return response.json();

}

async function fetch7TvEmoteImages() {
    const response = await fetch(
        `${API_ROOT_URL}/emotes/7tv/${broadcasterId}`
    );

    if (!response.ok) {
        throw new Error(`7TV emote request failed: ${response.status}`);
    }

    return response.json();
}

async function loadFollowedChannels() {
    const [response, accountResponse] = await Promise.all([
        fetch(
        `${API_ROOT_URL}/account/tracking-status`
        ),
        fetch(`${API_ROOT_URL}/account/status`)
    ]);

    if (!response.ok) {
        throw new Error(`Followed-channel status request failed: ${response.status}`);
    }
    if (!accountResponse.ok) {
        throw new Error(`Twitch account status request failed: ${accountResponse.status}`);
    }

    const status = await response.json();
    const accountStatus = await accountResponse.json();
    updateTwitchConnectionUi(accountStatus.connected);
    document.getElementById("followed-count").textContent =
        status.followedChannels.toLocaleString();
    document.getElementById("followed-live-count").textContent =
        status.liveChannels.toLocaleString();
    document.getElementById("followed-tracking-count").textContent =
        status.activelyTracked.toLocaleString();
    document.getElementById("followed-waiting-count").textContent =
        status.waitingForCapacity.toLocaleString();

    const container = document.getElementById("followed-channels");
    container.replaceChildren();

    const liveChannels = status.channels.filter(channel =>
        channel.trackingState === "Tracking" ||
        channel.trackingState === "Waiting");

    if (liveChannels.length === 0) {
        container.textContent = "No followed channels are currently live.";
        return;
    }

    function updateTwitchConnectionUi(connected) {
        const button = document.getElementById("connect-twitch");
        const status = document.getElementById("twitch-connection-status");
        const syncStatus = document.getElementById("followed-sync-status");

        button.textContent = connected ? "Connected" : "Connect Twitch";
        button.disabled = connected;
        button.classList.toggle("connected", connected);
        status.textContent = connected ? "Twitch user connected" : "";
        syncStatus.textContent = connected
            ? "Followed channels are synchronized."
            : "Connect Twitch to monitor followed channels.";
    }

    liveChannels.forEach(channel => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "followed-channel";

        const liveDot = document.createElement("span");
        liveDot.className = "live-channel-dot";
        liveDot.setAttribute("aria-label", "Live");

        const name = document.createElement("span");
        name.textContent = channel.login;

        const connection = document.createElement("span");
        connection.className = "followed-channel-status";
        connection.textContent = channel.connectionState === "Connected"
            ? "(connected)"
            : channel.connectionState === "Error"
                ? "(error)"
                : "(waiting)";

        button.append(liveDot, name, connection);
        button.addEventListener("click", async () => {
            await selectStreamer(channel.login);
            await loadDashboard();
        });
        container.appendChild(button);
    });
}

async function loadStreamHistory() {
    const selector = document.getElementById("stream-selector");
    selector.replaceChildren(new Option("All collected broadcasts", ""));
    selector.disabled = true;

    const response = await fetch(
        `${API_ROOT_URL}/streams/history/${broadcasterId}`
    );

    if (!response.ok) {
        throw new Error(`Stream history request failed: ${response.status}`);
    }

    const streams = await response.json();

    streams.forEach(stream => {
        const startedAt = new Date(stream.startedAt).toLocaleString();
        const status = stream.endedAt ? "Ended" : "Live";
        const title = stream.title ? ` - ${stream.title}` : "";

        selector.add(new Option(
            `${startedAt} (${status})${title}`,
            stream.id
        ));
    });

    selector.disabled = false;
}

function renderEmoteLeaderboard(emotes, emoteImages) {
    const container = document.getElementById("emote-leaderboard");
    container.replaceChildren();

    emotes.leaderboard.forEach(emote => {
        const row = document.createElement("div");
        row.className = "emote-row";

        const image = document.createElement("img");
        image.className = "emote-image";
        image.alt = emote.emoteName;
        image.loading = "lazy";

        const imageUrl = emoteImages[emote.emoteName];

        if (imageUrl) {
            image.src = imageUrl;
        } else {
            image.hidden = true;
        }

        const name = document.createElement("span");
        name.className = "emote-name";
        name.textContent = emote.emoteName;

        const count = document.createElement("span");
        count.className = "emote-count";
        count.textContent = emote.usageCount.toLocaleString();

        row.append(image, name, count);
        container.appendChild(row);
    });
}

let activityChart = null;

async function selectStreamer(username) {
    const response = await fetch(
        `${API_BASE_URL.replace("/chat", "")}/user/${encodeURIComponent(username)}`
    );

    if (!response.ok) {
        const error = await response.json().catch(() => null);
        throw new Error(error?.message ?? "Twitch user could not be found.");
    }

    const user = await response.json();
    broadcasterId = user.id;
    selectedStreamId = null;
    document.getElementById("streamer-username").value = user.login;
    const avatar = document.getElementById("streamer-avatar");
    avatar.src = user.profileImageUrl;
    avatar.alt = `${user.displayName} profile picture`;

    const streamResponse = await fetch(
        `${API_BASE_URL.replace("/chat", "")}/stream/${encodeURIComponent(user.login)}`
    );

    if (!streamResponse.ok) {
        throw new Error("The streamer was found, but their stream could not be prepared.");
    }

    const connectionResponse = await fetch(
        `${API_BASE_URL}/connect/${user.id}/start`,
        { method: "POST" }
    );

    if (!connectionResponse.ok) {
        throw new Error("The streamer was found, but chat collection could not be started.");
    }

    await loadStreamHistory();
}

function renderActivityChart(messagesPerMinute, uniqueChattersPerMinute) {
    const canvas = document.getElementById("activity-chart");

    const chatterCountsByMinute = new Map(
        uniqueChattersPerMinute.map(item => [
            new Date(item.minute).getTime(),
            item.uniqueChatters
        ])
    );

    const labels = messagesPerMinute.map(item =>
        new Date(item.minute).toLocaleTimeString([], {
            hour: "2-digit",
            minute: "2-digit"
        })
    );

    const messageCounts = messagesPerMinute.map(
        item => item.messageCount
    );

    const chatterCounts = messagesPerMinute.map(item =>
        chatterCountsByMinute.get(new Date(item.minute).getTime()) ?? 0
    );

    if (activityChart) {
        activityChart.data.labels = labels;
        activityChart.data.datasets[0].data = messageCounts;
        activityChart.data.datasets[1].data = chatterCounts;
        activityChart.update();
        return;
    }

    activityChart = new Chart(canvas, {
        type: "line",
        data: {
            labels: labels,
            datasets: [
                {
                    label: "Messages per minute",
                    data: messageCounts,
                    borderColor: "#a78bfa",
                    backgroundColor: "rgba(167, 139, 250, 0.12)",
                    borderWidth: 2,
                    fill: false,
                    tension: 0.3,
                    pointRadius: 3
                },
                {
                    label: "Unique chatters per minute",
                    data: chatterCounts,
                    borderColor: "#34d399",
                    backgroundColor: "rgba(52, 211, 153, 0.12)",
                    borderWidth: 2,
                    fill: false,
                    tension: 0.3,
                    pointRadius: 3
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                mode: "index",
                intersect: false
            },
            scales: {
                x: {
                    ticks: { color: "#a1a1aa" },
                    grid: { color: "rgba(161, 161, 170, 0.1)" }
                },
                y: {
                    beginAtZero: true,
                    ticks: { color: "#a1a1aa", precision: 0 },
                    grid: { color: "rgba(161, 161, 170, 0.1)" }
                }
            },
            plugins: {
                legend: {
                    labels: { color: "#e4e4e7" }
                },
                tooltip: {
                    callbacks: {
                        labelColor: context => ({
                            borderColor: context.dataset.borderColor,
                            backgroundColor: context.dataset.borderColor
                        }),
                        labelTextColor: () => "#ffffff"
                    }
                }
            }
        }
    });
}

async function loadDashboard() {
    try {
        
        const [
            totals,
            emotes,
            messagesPerMinute,
            emoteImages,
            uniqueChattersPerMinute
        ] = await Promise.all([
            fetchAnalytics("persistent"),
            fetchLeaderboard(),
            fetchAnalytics("messages-per-minute"),
            fetch7TvEmoteImages(),
            fetchAnalytics("unique-chatters-per-minute")
        ]);

        document.getElementById("total-messages").textContent =
            totals.totalMessages.toLocaleString();

        document.getElementById("unique-chatters").textContent =
            totals.uniqueChatters.toLocaleString();

        document.getElementById("unique-emotes").textContent =
            emotes.totalUniqueEmotes.toLocaleString();

        const messageCounts = messagesPerMinute.messagesPerMinute;
        renderActivityChart(
            messageCounts,
            uniqueChattersPerMinute.uniqueChattersPerMinute
        );
        renderEmoteLeaderboard(emotes, emoteImages);

        document.getElementById("messages-per-minute").textContent =
            messageCounts.length > 0
                ? messageCounts[messageCounts.length - 1].messageCount.toLocaleString()
                : "0";

        console.log("Dashboard analytics loaded successfully.");
    } catch (error) {
        console.error("Could not load dashboard analytics:", error);
    }
}

const streamerForm = document.getElementById("streamer-form");
const streamerError = document.getElementById("streamer-error");
const streamSelector = document.getElementById("stream-selector");
const connectTwitchButton = document.getElementById("connect-twitch");

const oauthParams = new URLSearchParams(window.location.search);
const oauthStatus = oauthParams.get("oauth");
const oauthMessage = oauthParams.get("message");
if (oauthStatus === "success" || oauthStatus === "error") {
    if (oauthStatus === "success") {
        updateTwitchConnectionUi(true);
        document.getElementById("twitch-connection-status").textContent =
            oauthMessage ?? "Twitch user connected";
    } else {
        streamerError.textContent = oauthMessage ??
            "Twitch authorization could not be completed.";
    }
    window.history.replaceState({}, document.title, window.location.pathname);
}

connectTwitchButton.addEventListener("click", async () => {
    const response = await fetch(
        `${API_ROOT_URL}/account/connect`
    );
    const result = await response.json();

    if (!response.ok) {
        streamerError.textContent =
            result.detail ?? "Twitch authorization could not be started.";
        return;
    }

    window.location.href = result.authorizationUrl;
});

streamSelector.addEventListener("change", async event => {
    selectedStreamId = event.target.value || null;
    await loadDashboard();
});

streamerForm.addEventListener("submit", async event => {
    event.preventDefault();
    streamerError.textContent = "";

    const username = document.getElementById("streamer-username").value.trim();

    if (!username) {
        streamerError.textContent = "Enter a Twitch username.";
        return;
    }

    const submitButton = streamerForm.querySelector("button");
    submitButton.disabled = true;

    try {
        await selectStreamer(username);
        await loadDashboard();
    } catch (error) {
        streamerError.textContent = error.message;
    } finally {
        submitButton.disabled = false;
    }
});

setInterval(() => {
    if (broadcasterId) {
        loadDashboard();
    }
    loadFollowedChannels().catch(error =>
        console.error("Could not load followed-channel status:", error));
}, 30000);

loadFollowedChannels().catch(error =>
    console.error("Could not load followed-channel status:", error));
