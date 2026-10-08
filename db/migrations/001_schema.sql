-- 001_schema.sql  (IMPLEMENTATION_PLAN step 2)
-- Runs as the owner/superuser (POSTGRES_USER) via docker-entrypoint-initdb.d.
-- UNVERIFIED: never executed against a real Postgres. First run must be against
-- pgvector/pgvector:pg16.
--
-- Reverse with: db/down/001_schema.down.sql   (destroys all data - dev only)

CREATE EXTENSION IF NOT EXISTS vector;

-- ---------------------------------------------------------------------------
-- Role hierarchy: the ONE place the hierarchy is defined.
-- employee(1) < manager(2) < hr-admin(3). A higher level inherits every lower
-- one, so "can see" is simply required_level <= caller_level.
-- ---------------------------------------------------------------------------
CREATE TABLE roles (
    name  text     PRIMARY KEY CHECK (name = lower(name)),
    level smallint NOT NULL UNIQUE CHECK (level >= 1)
);

INSERT INTO roles (name, level) VALUES
    ('employee', 1),
    ('manager',  2),
    ('hr-admin', 3);

-- Role name -> level. NULL for an unknown role (callers must treat NULL as
-- "no access"; see rag_level_allows).
CREATE FUNCTION rag_role_level(p_role text)
RETURNS integer
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT r.level::integer FROM public.roles r WHERE r.name = p_role
$$;

-- THE shared expansion. Both the application filter and the RLS policy call
-- this, so the two layers cannot disagree about the hierarchy.
-- NULL caller level (unset / unknown role) or NULL required level => false.
CREATE FUNCTION rag_level_allows(p_caller_level integer, p_required_level integer)
RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $$
    SELECT coalesce(p_required_level <= p_caller_level, false)
$$;

-- ---------------------------------------------------------------------------
-- Tenancy and identity
-- ---------------------------------------------------------------------------
CREATE TABLE tenants (
    id         uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    slug       text        NOT NULL UNIQUE,
    name       text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (
    id         uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id  uuid        NOT NULL REFERENCES tenants (id),
    email      text        NOT NULL,
    role       text        NOT NULL REFERENCES roles (name),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, email),
    UNIQUE (id, tenant_id)      -- lets audit_log prove user belongs to the tenant
);

-- ---------------------------------------------------------------------------
-- Documents carry the ACL. Chunks never do: access is joined via doc_id at
-- query time, so a revoke / level change needs no re-embed.
-- required_level only (no allowed_roles array): a flat role list contradicts
-- the hierarchy and gives the app filter and RLS two things to keep in sync.
-- No DEFAULT on required_level on purpose: forgetting it is an error, not an
-- accidentally-public document.
-- ---------------------------------------------------------------------------
CREATE TABLE documents (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid        NOT NULL REFERENCES tenants (id),
    title          text        NOT NULL,
    required_level smallint    NOT NULL REFERENCES roles (level),
    version        integer     NOT NULL DEFAULT 1,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    UNIQUE (id, tenant_id)      -- target of the composite FK from chunks
);

CREATE INDEX documents_tenant_idx ON documents (tenant_id);

-- Embedding dimension 768 = nomic-embed-text. Sticky: changing it means a
-- new column/index and a full re-embed.
CREATE TABLE chunks (
    id          uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    doc_id      uuid         NOT NULL,
    tenant_id   uuid         NOT NULL,
    chunk_index integer      NOT NULL CHECK (chunk_index >= 0),
    text        text         NOT NULL,
    embedding   vector(768)  NOT NULL,
    version     integer      NOT NULL DEFAULT 1,
    created_at  timestamptz  NOT NULL DEFAULT now(),
    -- Composite FK: a chunk's tenant_id MUST equal its document's tenant_id.
    FOREIGN KEY (doc_id, tenant_id) REFERENCES documents (id, tenant_id) ON DELETE CASCADE,
    UNIQUE (doc_id, version, chunk_index)
);

CREATE INDEX chunks_doc_idx    ON chunks (doc_id);
CREATE INDEX chunks_tenant_idx ON chunks (tenant_id);

-- Approximate nearest neighbour, cosine distance (query with  embedding <=> $1).
CREATE INDEX chunks_embedding_hnsw ON chunks
    USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

-- ---------------------------------------------------------------------------
-- Audit log: one row per query. Retrieved vs sent-to-LLM stored SEPARATELY.
-- Append-only is enforced by GRANTs in 002_rls.sql (no UPDATE/DELETE for app_user).
-- ---------------------------------------------------------------------------
CREATE TABLE audit_log (
    id                  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at         timestamptz NOT NULL DEFAULT now(),
    tenant_id           uuid        NOT NULL,
    user_id             uuid        NOT NULL,
    role                text        NOT NULL REFERENCES roles (name),
    query_text          text,                       -- optional; hash is mandatory
    query_hash          text        NOT NULL,
    retrieved_chunk_ids uuid[]      NOT NULL DEFAULT '{}',
    sent_chunk_ids      uuid[]      NOT NULL DEFAULT '{}',
    model               text        NOT NULL,
    latency_ms          integer     NOT NULL CHECK (latency_ms >= 0),
    FOREIGN KEY (user_id, tenant_id) REFERENCES users (id, tenant_id),
    -- Whatever went to the LLM must have been retrieved first.
    CHECK (sent_chunk_ids <@ retrieved_chunk_ids)
);

CREATE INDEX audit_log_tenant_time_idx ON audit_log (tenant_id, occurred_at DESC);
