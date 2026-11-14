import Foundation
import StoreKit

@_silgen_name("UnitySendMessage")
private func UnitySendMessage(_ object: UnsafePointer<CChar>, _ method: UnsafePointer<CChar>, _ message: UnsafePointer<CChar>)

@available(iOS 15.0, *)
@MainActor private final class OrcubStore {
    static let shared = OrcubStore()
    var receiver = "OrcubStore"
    var listener: Task<Void, Never>?
    var transactions: [String: Transaction] = [:]

    func send(_ value: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: value), let json = String(data: data, encoding: .utf8) else { return }
        receiver.withCString { object in "StoreResult".withCString { method in json.withCString { UnitySendMessage(object, method, $0) } } }
    }
    func record(_ result: VerificationResult<Transaction>) throws -> [String: Any] {
        // Backend remains the authority; never send an unverified device result as an entitlement.
        guard case .verified(let transaction) = result else { throw StoreError.unverified }
        let id = String(transaction.id)
        transactions[id] = transaction
        return ["Id": id, "ProductId": transaction.productID, "Jws": result.jwsRepresentation]
    }
    enum StoreError: Error { case unverified, unavailable, account }
    func call(_ request: [String: Any]) async {
        let id = request["id"] as? String ?? ""
        let payload = request["payload"] as? [String: Any] ?? [:]
        if listener == nil {
            listener = Task { for await result in Transaction.updates {
                do { self.send(["id": "update", "transaction": try self.record(result)]) }
                catch { self.send(["id": "update_error", "error": "unverified"]) }
            } }
        }
        do {
            var response: [String: Any] = ["id": id]
            switch request["action"] as? String {
            case "products":
                let products = try await Product.products(for: payload["ids"] as? [String] ?? [])
                response["products"] = products.map { ["Id": $0.id, "Title": $0.displayName, "LocalizedPrice": $0.displayPrice] }
            case "purchase":
                guard let uuid = UUID(uuidString: payload["account"] as? String ?? "") else { throw StoreError.account }
                guard let product = try await Product.products(for: [payload["product"] as? String ?? ""]).first else { throw StoreError.unavailable }
                switch try await product.purchase(options: [.appAccountToken(uuid)]) {
                case .success(let result): response["transaction"] = try record(result)
                case .pending: break
                case .userCancelled: response["error"] = "cancelled"
                @unknown default: response["error"] = "unknown"
                }
            case "unfinished", "restore":
                var items: [[String: Any]] = []
                if request["action"] as? String == "restore" {
                    try await AppStore.sync()
                    for await result in Transaction.currentEntitlements { items.append(try record(result)) }
                } else {
                    for await result in Transaction.unfinished { items.append(try record(result)) }
                }
                response["transactions"] = items
            case "finish":
                guard let transaction = transactions[payload["transaction"] as? String ?? ""] else { throw StoreError.unavailable }
                await transaction.finish()
                transactions.removeValue(forKey: String(transaction.id))
            default: throw StoreError.unavailable
            }
            send(response)
        } catch { send(["id": id, "error": "store_failed"]) }
    }
}

@_cdecl("OrcubStoreCall")
public func OrcubStoreCall(_ receiver: UnsafePointer<CChar>, _ json: UnsafePointer<CChar>) {
    let name = String(cString: receiver)
    let data = Data(String(cString: json).utf8)
    guard let request = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
    if #available(iOS 15.0, *) {
        Task { @MainActor in
            OrcubStore.shared.receiver = name
            await OrcubStore.shared.call(request)
        }
    }
}
