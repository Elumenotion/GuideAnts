import { Link } from 'react-router-dom';

const Privacy: React.FC = () => {
    return (
        <div className="h-screen overflow-y-auto bg-gray-50 text-gray-900">
            <div className="max-w-3xl mx-auto px-6 py-10">
                <header className="mb-6">
                    <Link to="/login" className="inline-flex items-center text-sm text-blue-700 hover:underline">
                        ← Back to Sign in
                    </Link>
                </header>

                <div className="bg-white rounded-xl shadow-sm border border-gray-200">
                    <div className="px-6 py-6 border-b border-gray-100 flex items-center gap-3">
                        <img src="/guide.png" alt="GuideAnts logo" className="w-8 h-8" />
                        <div>
                            <h1 className="text-2xl font-bold">Privacy Policy</h1>
                            <p className="text-sm text-gray-600">Effective date: January 1, 2025</p>
                        </div>
                    </div>

                    <div className="px-6 py-6 space-y-8">
                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Overview</h2>
                            <p className="text-gray-700 leading-7">
                                This Privacy Policy explains how GuideAnts ("we", "us", "our") collects, uses, and safeguards
                                your information when you use GuideAnts Notebooks (the "Service").
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Information We Collect</h2>
                            <ul className="list-disc pl-6 text-gray-700 leading-7 space-y-1">
                                <li>Account information from Microsoft sign-in (name, email, tenant ID).</li>
                                <li>Usage and diagnostic data to improve performance and reliability.</li>
                                <li>Content you provide to the Service (projects, notebooks, files).</li>
                            </ul>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">How We Use Information</h2>
                            <ul className="list-disc pl-6 text-gray-700 leading-7 space-y-1">
                                <li>To operate, maintain, and improve the Service.</li>
                                <li>To provide features, including collaboration and AI-assisted capabilities.</li>
                                <li>To secure the Service, detect abuse, and comply with legal obligations.</li>
                            </ul>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Data Retention</h2>
                            <p className="text-gray-700 leading-7">
                                We retain information for as long as necessary to provide the Service and comply with applicable
                                laws. You can request deletion of your data subject to legal and operational constraints.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Data Security</h2>
                            <p className="text-gray-700 leading-7">
                                We implement administrative, technical, and physical safeguards designed to protect your data.
                                No method of transmission or storage is 100% secure; we strive for best practices.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Third Parties</h2>
                            <p className="text-gray-700 leading-7">
                                We may use trusted processors and cloud services to operate the Service. We do not sell your
                                personal information.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Your Choices</h2>
                            <ul className="list-disc pl-6 text-gray-700 leading-7 space-y-1">
                                <li>Access, update, or delete your account information.</li>
                                <li>Export or delete your content, subject to retention and legal requirements.</li>
                                <li>Contact us regarding privacy requests at privacy@guideants.ai.</li>
                            </ul>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Children's Privacy</h2>
                            <p className="text-gray-700 leading-7">
                                The Service is not directed to children under 13 and we do not knowingly collect information from
                                children.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Changes to this Policy</h2>
                            <p className="text-gray-700 leading-7">
                                We may update this Privacy Policy from time to time. Material changes will be communicated, and
                                continued use of the Service signifies acceptance of the updated policy.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">Contact</h2>
                            <p className="text-gray-700 leading-7">
                                Questions about this Privacy Policy can be directed to privacy@guideants.ai.
                            </p>
                        </section>
                    </div>

                    <div className="px-6 py-6 border-t border-gray-100 text-sm text-gray-600">
                        © {new Date().getFullYear()} Elumenotion, LLC. All rights reserved.
                    </div>
                </div>

                <footer className="mt-6 text-xs text-gray-500 text-center">
                    © {new Date().getFullYear()} Elumenotion, LLC. All rights reserved.
                </footer>
            </div>
        </div>
    );
};

export default Privacy;


